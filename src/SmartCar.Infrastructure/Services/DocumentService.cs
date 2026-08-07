using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class DocumentService : IDocumentService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public DocumentService(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<DocumentDto>> GetCustomerDocumentsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => document.CustomerId == customerId)
            .OrderBy(document => document.DocumentType)
            .ToListAsync(cancellationToken);

        return await MapDocumentsAsync(documents, cancellationToken);
    }

    public async Task<DocumentDto?> GetDocumentAsync(
        int documentId,
        CancellationToken cancellationToken = default)
    {
        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => document.CustomerDocumentId == documentId)
            .ToListAsync(cancellationToken);

        var mapped = await MapDocumentsAsync(documents, cancellationToken);
        return mapped.SingleOrDefault();
    }

    public async Task<IReadOnlyList<DocumentDto>> GetPendingDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => document.Status == DocumentStatus.Pending)
            .OrderBy(document => document.UpdatedAt)
            .ToListAsync(cancellationToken);

        return await MapDocumentsAsync(documents, cancellationToken);
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

        var normalizedNumber = request.DocumentNumber.Trim().ToUpperInvariant();
        var isCitizenId = request.DocumentType is DocumentTypes.CitizenId or DocumentTypes.CitizenIdBack;

        if (isCitizenId)
        {
            var pairedType = request.DocumentType == DocumentTypes.CitizenId
                ? DocumentTypes.CitizenIdBack
                : DocumentTypes.CitizenId;

            var pairedNumber = await _dbContext.CustomerDocuments
                .AsNoTracking()
                .Where(item =>
                    item.CustomerId == customerId &&
                    item.DocumentType == pairedType)
                .Select(item => item.DocumentNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(pairedNumber) &&
                !string.Equals(pairedNumber, normalizedNumber, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Failure(
                    "Số CCCD mặt trước và mặt sau phải trùng nhau.");
            }
        }

        var duplicate = isCitizenId
            ? await _dbContext.CustomerDocuments
                .AsNoTracking()
                .AnyAsync(item =>
                    item.CustomerId != customerId &&
                    (item.DocumentType == DocumentTypes.CitizenId ||
                     item.DocumentType == DocumentTypes.CitizenIdBack) &&
                    item.DocumentNumber == normalizedNumber,
                    cancellationToken)
            : await _dbContext.CustomerDocuments
                .AsNoTracking()
                .AnyAsync(item =>
                    item.CustomerId != customerId &&
                    item.DocumentType == request.DocumentType &&
                    item.DocumentNumber == normalizedNumber,
                    cancellationToken);

        if (duplicate)
        {
            return OperationResult.Failure("Số giấy tờ đã được sử dụng bởi tài khoản khác.");
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

        document.DocumentNumber = normalizedNumber;
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

        await _auditService.WriteAsync(
            customerId,
            "Submit",
            nameof(CustomerDocument),
            document.CustomerDocumentId.ToString(),
            $"Gửi {document.DocumentType} để xác minh.",
            cancellationToken: cancellationToken);

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

        var previousStatus = document.Status;

        if (verified && previousStatus != DocumentStatus.Pending)
        {
            return OperationResult.Failure("Chỉ giấy tờ đang chờ xác minh mới có thể được xác minh.");
        }

        if (!verified && previousStatus is not DocumentStatus.Pending and not DocumentStatus.Verified)
        {
            return OperationResult.Failure("Giấy tờ hiện không thể chuyển sang trạng thái cần cập nhật.");
        }

        if (!verified && string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do yêu cầu gửi lại hoặc cập nhật giấy tờ.");
        }

        var requestedUpdateForVerifiedDocument = !verified && previousStatus == DocumentStatus.Verified;

        document.Status = verified ? DocumentStatus.Verified : DocumentStatus.Rejected;
        document.RejectionReason = verified ? null : reason!.Trim();
        document.VerifiedBy = verified ? adminId : null;
        document.VerifiedAt = verified ? DateTime.UtcNow : null;
        document.UpdatedAt = DateTime.UtcNow;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = document.CustomerId,
            Title = verified
                ? "Giấy tờ đã được xác minh"
                : requestedUpdateForVerifiedDocument
                    ? "Giấy tờ cần được cập nhật"
                    : "Cần gửi lại giấy tờ",
            Message = verified
                ? $"{document.DocumentType} của bạn đã được Quản trị viên xác minh."
                : $"{document.DocumentType} cần được cập nhật và gửi lại. Lý do: {document.RejectionReason}"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            verified
                ? "Verify"
                : requestedUpdateForVerifiedDocument
                    ? "RequestUpdate"
                    : "RequestResubmission",
            nameof(CustomerDocument),
            document.CustomerDocumentId.ToString(),
            verified
                ? $"Xác minh {document.DocumentType} của khách hàng {document.CustomerId}."
                : $"Yêu cầu khách hàng {document.CustomerId} cập nhật và gửi lại {document.DocumentType}: {document.RejectionReason}",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private async Task<IReadOnlyList<DocumentDto>> MapDocumentsAsync(
        IReadOnlyCollection<CustomerDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return Array.Empty<DocumentDto>();
        }

        var customerIds = documents
            .Select(document => document.CustomerId)
            .Distinct()
            .ToList();

        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(user => customerIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.PhoneNumber
            })
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        return documents
            .Select(document =>
            {
                users.TryGetValue(document.CustomerId, out var user);

                return new DocumentDto(
                    document.CustomerDocumentId,
                    document.CustomerId,
                    user?.FullName ?? string.Empty,
                    user?.PhoneNumber,
                    document.DocumentType,
                    document.DocumentNumber,
                    document.ExpiryDate,
                    document.ImagePath,
                    document.Status,
                    document.RejectionReason,
                    document.VerifiedBy,
                    document.VerifiedAt,
                    document.UpdatedAt);
            })
            .ToList();
    }

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
