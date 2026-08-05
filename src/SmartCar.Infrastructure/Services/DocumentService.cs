using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class DocumentService : IDocumentService
{
    private readonly ApplicationDbContext _dbContext;

    public DocumentService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<DocumentDto>> GetCustomerDocumentsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var documents = await QueryDocuments()
            .Where(document => document.CustomerId == customerId)
            .OrderBy(document => document.DocumentType)
            .ToListAsync(cancellationToken);

        return documents;
    }

    public async Task<IReadOnlyList<DocumentDto>> GetPendingDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await QueryDocuments()
            .Where(document => document.Status == DocumentStatus.Pending)
            .OrderBy(document => document.UpdatedAt)
            .ToListAsync(cancellationToken);

        return documents;
    }

    public async Task<OperationResult> SubmitAsync(
        string customerId,
        SubmitDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DocumentTypes.RequiredForRental.Contains(request.DocumentType))
        {
            return OperationResult.Failure("Loại giấy tờ không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(request.DocumentNumber) ||
            string.IsNullOrWhiteSpace(request.ImagePath))
        {
            return OperationResult.Failure("Số giấy tờ và ảnh giấy tờ là bắt buộc.");
        }

        if (request.DocumentType == DocumentTypes.DrivingLicense &&
            (!request.ExpiryDate.HasValue || request.ExpiryDate.Value.Date < DateTime.Today))
        {
            return OperationResult.Failure("GPLX phải còn thời hạn sử dụng.");
        }

        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(item =>
                item.CustomerId == customerId &&
                item.DocumentType == request.DocumentType,
                cancellationToken);

        if (document is null)
        {
            document = new CustomerDocument
            {
                CustomerId = customerId,
                DocumentType = request.DocumentType,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.CustomerDocuments.Add(document);
        }

        document.DocumentNumber = request.DocumentNumber.Trim().ToUpperInvariant();
        document.ExpiryDate = request.ExpiryDate;
        document.ImagePath = request.ImagePath;
        document.Status = DocumentStatus.Pending;
        document.RejectionReason = null;
        document.VerifiedBy = null;
        document.VerifiedAt = null;
        document.UpdatedAt = DateTime.UtcNow;

        await NotifyAdminsAsync(
            "Có giấy tờ chờ xác minh",
            $"Khách hàng vừa gửi {request.DocumentType} để xác minh.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public Task<OperationResult> VerifyAsync(
        int documentId,
        string adminId,
        CancellationToken cancellationToken = default) =>
        DecideAsync(documentId, adminId, true, null, cancellationToken);

    public Task<OperationResult> RejectAsync(
        int documentId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default) =>
        DecideAsync(documentId, adminId, false, reason, cancellationToken);

    public async Task<bool> HasValidRentalDocumentsAsync(
        string customerId,
        DateTime rentalDate,
        CancellationToken cancellationToken = default)
    {
        var validTypes = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == customerId &&
                document.Status == DocumentStatus.Verified &&
                (!document.ExpiryDate.HasValue || document.ExpiryDate.Value.Date >= rentalDate.Date))
            .Select(document => document.DocumentType)
            .Distinct()
            .ToListAsync(cancellationToken);

        return DocumentTypes.RequiredForRental.All(validTypes.Contains);
    }

    private async Task<OperationResult> DecideAsync(
        int documentId,
        string adminId,
        bool verified,
        string? reason,
        CancellationToken cancellationToken)
    {
        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(item => item.CustomerDocumentId == documentId, cancellationToken);

        if (document is null)
        {
            return OperationResult.Failure("Không tìm thấy giấy tờ.");
        }

        if (!verified && string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do từ chối.");
        }

        document.Status = verified ? DocumentStatus.Verified : DocumentStatus.Rejected;
        document.RejectionReason = verified ? null : reason!.Trim();
        document.VerifiedBy = adminId;
        document.VerifiedAt = DateTime.UtcNow;
        document.UpdatedAt = DateTime.UtcNow;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = document.CustomerId,
            Title = verified ? "Giấy tờ đã được xác minh" : "Giấy tờ bị từ chối",
            Message = verified
                ? $"{document.DocumentType} của bạn đã được Admin xác minh."
                : $"{document.DocumentType} bị từ chối. Lý do: {document.RejectionReason}"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private IQueryable<DocumentDto> QueryDocuments() =>
        from document in _dbContext.CustomerDocuments.AsNoTracking()
        join user in _dbContext.Users.AsNoTracking()
            on document.CustomerId equals user.Id into userGroup
        from user in userGroup.DefaultIfEmpty()
        select new DocumentDto(
            document.CustomerDocumentId,
            document.CustomerId,
            user == null ? string.Empty : user.FullName,
            user == null ? null : user.PhoneNumber,
            document.DocumentType,
            document.DocumentNumber,
            document.ExpiryDate,
            document.ImagePath,
            document.Status,
            document.RejectionReason,
            document.VerifiedBy,
            document.VerifiedAt,
            document.UpdatedAt);

    private async Task NotifyAdminsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var adminRoleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .Where(item => item.RoleId == adminRoleId)
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }
}
