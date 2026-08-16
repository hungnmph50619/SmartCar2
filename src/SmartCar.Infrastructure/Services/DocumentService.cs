using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

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

        var normalizedNumber = request.DocumentNumber.Trim().ToUpperInvariant();
        var duplicate = await _dbContext.CustomerDocuments
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

        var document = await GetOrCreateDocumentAsync(
            customerId,
            request.DocumentType,
            cancellationToken);

        document.DocumentNumber = normalizedNumber;
        document.ExpiryDate = request.ExpiryDate;
        document.ImagePath = request.ImagePath;
        ResetToPending(document);

        await NotifyAdminsAsync(
            "Có giấy tờ chờ xác minh",
            $"Khách hàng vừa gửi {request.DocumentType} để xác minh.",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SubmitCitizenIdAsync(
        string customerId,
        SubmitCitizenIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateCitizenId(request);
        if (validationError is not null)
        {
            return OperationResult.Failure(validationError);
        }

        var normalizedNumber = request.DocumentNumber.Trim();
        var duplicate = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(item =>
                item.CustomerId != customerId &&
                (item.DocumentType == DocumentTypes.CitizenId ||
                 item.DocumentType == DocumentTypes.CitizenIdBack) &&
                item.DocumentNumber == normalizedNumber,
                cancellationToken);

        if (duplicate)
        {
            return OperationResult.Failure("Số CCCD đã được sử dụng bởi tài khoản khác.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var front = await GetOrCreateDocumentAsync(
            customerId,
            DocumentTypes.CitizenId,
            cancellationToken);
        var back = await GetOrCreateDocumentAsync(
            customerId,
            DocumentTypes.CitizenIdBack,
            cancellationToken);

        front.DocumentNumber = normalizedNumber;
        front.ExpiryDate = request.ExpiryDate.Date;
        front.ImagePath = request.FrontImagePath;
        ResetToPending(front);

        back.DocumentNumber = normalizedNumber;
        back.ExpiryDate = request.ExpiryDate.Date;
        back.ImagePath = request.BackImagePath;
        ResetToPending(back);

        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var document in new[] { front, back })
        {
            await UpdateKycMetadataAsync(
                document.CustomerDocumentId,
                request.FullNameOnDocument.Trim(),
                request.DateOfBirth.Date,
                request.Gender.Trim(),
                request.IssuedDate.Date,
                null,
                null,
                cancellationToken);
        }

        await NotifyAdminsAsync(
            "Có CCCD chờ xác minh",
            "Khách hàng vừa gửi thông tin CCCD kèm ảnh mặt trước và mặt sau để xác minh.",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "Submit",
            nameof(CustomerDocument),
            front.CustomerDocumentId.ToString(),
            "Gửi hồ sơ CCCD gồm thông tin khai báo và hai ảnh để xác minh.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SubmitDrivingLicenseAsync(
        string customerId,
        SubmitDrivingLicenseRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateDrivingLicense(request);
        if (validationError is not null)
        {
            return OperationResult.Failure(validationError);
        }

        var normalizedNumber = request.DocumentNumber.Trim().ToUpperInvariant();
        var duplicate = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(item =>
                item.CustomerId != customerId &&
                (item.DocumentType == DocumentTypes.DrivingLicense ||
                 item.DocumentType == DocumentTypes.DrivingLicenseBack) &&
                item.DocumentNumber == normalizedNumber,
                cancellationToken);

        if (duplicate)
        {
            return OperationResult.Failure("Số GPLX đã được sử dụng bởi tài khoản khác.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var front = await GetOrCreateDocumentAsync(
            customerId,
            DocumentTypes.DrivingLicense,
            cancellationToken);
        var back = await GetOrCreateDocumentAsync(
            customerId,
            DocumentTypes.DrivingLicenseBack,
            cancellationToken);

        front.DocumentNumber = normalizedNumber;
        front.ExpiryDate = request.ExpiryDate.Date;
        front.ImagePath = request.FrontImagePath;
        ResetToPending(front);

        back.DocumentNumber = normalizedNumber;
        back.ExpiryDate = request.ExpiryDate.Date;
        back.ImagePath = request.BackImagePath;
        ResetToPending(back);

        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var document in new[] { front, back })
        {
            await UpdateKycMetadataAsync(
                document.CustomerDocumentId,
                request.FullNameOnDocument.Trim(),
                null,
                null,
                request.IssuedDate.Date,
                null,
                request.LicenseClass.Trim().ToUpperInvariant(),
                cancellationToken);
        }

        await NotifyAdminsAsync(
            "Có GPLX chờ xác minh",
            "Khách hàng vừa gửi đầy đủ thông tin cùng ảnh mặt trước và mặt sau GPLX để xác minh.",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "Submit",
            nameof(CustomerDocument),
            front.CustomerDocumentId.ToString(),
            "Gửi thông tin GPLX cùng ảnh mặt trước và mặt sau để xác minh.",
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
        var documents = await GetCustomerDocumentsAsync(customerId, cancellationToken);
        var citizenFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var citizenBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);
        var licenseFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);
        var licenseBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicenseBack);

        var citizenValid = citizenFront is not null &&
                           citizenBack is not null &&
                           citizenFront.Status == DocumentStatus.Verified &&
                           citizenBack.Status == DocumentStatus.Verified &&
                           citizenFront.HasRequiredData &&
                           citizenBack.HasRequiredData &&
                           citizenFront.ExpiryDate.HasValue &&
                           citizenFront.ExpiryDate.Value.Date >= rentalDate.Date;

        var licenseValid = licenseFront is not null &&
                           licenseBack is not null &&
                           licenseFront.Status == DocumentStatus.Verified &&
                           licenseBack.Status == DocumentStatus.Verified &&
                           licenseFront.HasRequiredData &&
                           licenseBack.HasRequiredData &&
                           licenseFront.ExpiryDate.HasValue &&
                           licenseFront.ExpiryDate.Value.Date >= rentalDate.Date;

        return citizenValid && licenseValid;
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

        var metadata = await ReadKycMetadataAsync(document.CustomerDocumentId, cancellationToken);
        if (verified && !HasRequiredData(document, metadata))
        {
            return OperationResult.Failure("Giấy tờ chưa có đủ thông tin cần thiết để xác minh.");
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

    private async Task<CustomerDocument> GetOrCreateDocumentAsync(
        string customerId,
        string documentType,
        CancellationToken cancellationToken)
    {
        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(item =>
                item.CustomerId == customerId &&
                item.DocumentType == documentType,
                cancellationToken);

        if (document is not null)
        {
            return document;
        }

        document = new CustomerDocument
        {
            CustomerId = customerId,
            DocumentType = documentType,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.CustomerDocuments.Add(document);
        return document;
    }

    private static void ResetToPending(CustomerDocument document)
    {
        document.Status = DocumentStatus.Pending;
        document.RejectionReason = null;
        document.VerifiedBy = null;
        document.VerifiedAt = null;
        document.UpdatedAt = DateTime.UtcNow;
    }

    private static string? ValidateCitizenId(SubmitCitizenIdRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullNameOnDocument) ||
            string.IsNullOrWhiteSpace(request.DocumentNumber) ||
            string.IsNullOrWhiteSpace(request.Gender) ||
            string.IsNullOrWhiteSpace(request.FrontImagePath) ||
            string.IsNullOrWhiteSpace(request.BackImagePath))
        {
            return "Vui lòng nhập đầy đủ thông tin trên CCCD và tải cả hai mặt CCCD.";
        }

        if (request.DocumentNumber.Length != 12 || !request.DocumentNumber.All(char.IsDigit))
        {
            return "Số CCCD phải gồm đúng 12 chữ số.";
        }

        if (request.DateOfBirth.Date > DateTime.Today.AddYears(-18))
        {
            return "Khách thuê xe phải đủ 18 tuổi.";
        }

        if (request.IssuedDate.Date > DateTime.Today)
        {
            return "Ngày cấp CCCD không được sau ngày hiện tại.";
        }

        if (request.ExpiryDate.Date < DateTime.Today)
        {
            return "CCCD đã hết hạn. Vui lòng sử dụng CCCD còn hiệu lực.";
        }

        if (request.ExpiryDate.Date <= request.IssuedDate.Date)
        {
            return "Ngày hết hạn CCCD phải sau ngày cấp.";
        }

        return null;
    }

    private static string? ValidateDrivingLicense(SubmitDrivingLicenseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullNameOnDocument) ||
            string.IsNullOrWhiteSpace(request.DocumentNumber) ||
            string.IsNullOrWhiteSpace(request.LicenseClass) ||
            string.IsNullOrWhiteSpace(request.FrontImagePath) ||
            string.IsNullOrWhiteSpace(request.BackImagePath))
        {
            return "Vui lòng nhập đầy đủ thông tin và tải cả mặt trước lẫn mặt sau GPLX.";
        }

        if (request.IssuedDate.Date > DateTime.Today)
        {
            return "Ngày cấp GPLX không được sau ngày hiện tại.";
        }

        if (request.ExpiryDate.Date < DateTime.Today)
        {
            return "GPLX đã hết hạn. Vui lòng sử dụng GPLX còn hiệu lực.";
        }

        if (request.ExpiryDate.Date <= request.IssuedDate.Date)
        {
            return "Ngày hết hạn GPLX phải sau ngày cấp.";
        }

        return null;
    }

    private async Task UpdateKycMetadataAsync(
        int documentId,
        string? fullNameOnDocument,
        DateTime? dateOfBirth,
        string? gender,
        DateTime? issuedDate,
        string? permanentAddress,
        string? licenseClass,
        CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE [CustomerDocuments]
            SET [FullNameOnDocument] = {fullNameOnDocument},
                [DateOfBirth] = {dateOfBirth},
                [Gender] = {gender},
                [IssuedDate] = {issuedDate},
                [PermanentAddress] = {permanentAddress},
                [LicenseClass] = {licenseClass}
            WHERE [CustomerDocumentId] = {documentId}", cancellationToken);
    }

    private async Task<KycMetadata> ReadKycMetadataAsync(
        int documentId,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();

            // Nếu DbContext hiện đang nằm trong transaction,
            // command này phải tham gia cùng transaction đó.
            if (_dbContext.Database.CurrentTransaction is { } currentTransaction)
            {
                command.Transaction = currentTransaction.GetDbTransaction();
            }

            command.CommandText = @"
            SELECT
                [FullNameOnDocument],
                [DateOfBirth],
                [Gender],
                [IssuedDate],
                [PermanentAddress],
                [LicenseClass]
            FROM [CustomerDocuments]
            WHERE [CustomerDocumentId] = @documentId";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@documentId";
            parameter.Value = documentId;
            command.Parameters.Add(parameter);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return KycMetadata.Empty;
            }

            return new KycMetadata(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static bool HasRequiredData(CustomerDocument document, KycMetadata metadata) =>
        document.DocumentType switch
        {
            DocumentTypes.CitizenId =>
                !string.IsNullOrWhiteSpace(document.DocumentNumber) &&
                !string.IsNullOrWhiteSpace(document.ImagePath) &&
                document.ExpiryDate.HasValue &&
                !string.IsNullOrWhiteSpace(metadata.FullNameOnDocument) &&
                metadata.DateOfBirth.HasValue,
            DocumentTypes.CitizenIdBack =>
                !string.IsNullOrWhiteSpace(document.DocumentNumber) &&
                !string.IsNullOrWhiteSpace(document.ImagePath),
            DocumentTypes.DrivingLicense =>
                !string.IsNullOrWhiteSpace(document.DocumentNumber) &&
                !string.IsNullOrWhiteSpace(document.ImagePath) &&
                document.ExpiryDate.HasValue &&
                !string.IsNullOrWhiteSpace(metadata.FullNameOnDocument) &&
                !string.IsNullOrWhiteSpace(metadata.LicenseClass),
            DocumentTypes.DrivingLicenseBack =>
                !string.IsNullOrWhiteSpace(document.DocumentNumber) &&
                !string.IsNullOrWhiteSpace(document.ImagePath),
            _ => false
        };

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

        var result = new List<DocumentDto>(documents.Count);
        foreach (var document in documents)
        {
            users.TryGetValue(document.CustomerId, out var user);
            var metadata = await ReadKycMetadataAsync(
                document.CustomerDocumentId,
                cancellationToken);

            result.Add(new DocumentDto(
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
                document.UpdatedAt,
                metadata.FullNameOnDocument,
                metadata.DateOfBirth,
                metadata.Gender,
                metadata.IssuedDate,
                metadata.PermanentAddress,
                metadata.LicenseClass,
                HasRequiredData(document, metadata)));
        }

        return result;
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

    private sealed record KycMetadata(
        string? FullNameOnDocument,
        DateTime? DateOfBirth,
        string? Gender,
        DateTime? IssuedDate,
        string? PermanentAddress,
        string? LicenseClass)
    {
        public static readonly KycMetadata Empty = new(null, null, null, null, null, null);
    }
}
