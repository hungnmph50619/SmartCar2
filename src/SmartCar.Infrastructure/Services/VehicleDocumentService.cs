using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.VehicleDocuments;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class VehicleDocumentService : IVehicleDocumentService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public VehicleDocumentService(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    // Tên tiếng Việt cho từng loại giấy tờ, dùng cho thông báo lỗi/nhật ký thao tác hiển thị
    // cho Admin — tránh lộ tên enum kỹ thuật (Inspection, Insurance...) ra giao diện.
    // Khớp đúng với SmartCar.Web.Extensions.VietnameseDisplayExtensions.ToVietnamese()
    // (không tham chiếu trực tiếp được vì Infrastructure không phụ thuộc ngược vào Web).
    private static string ToVietnameseName(VehicleDocumentType documentType) => documentType switch
    {
        VehicleDocumentType.Registration => "Đăng ký xe",
        VehicleDocumentType.Inspection => "Đăng kiểm",
        VehicleDocumentType.Insurance => "Bảo hiểm",
        VehicleDocumentType.RoadFee => "Phí sử dụng đường bộ",
        VehicleDocumentType.Other => "Giấy tờ khác",
        _ => documentType.ToString()
    };

    private static bool RequiresSingleActiveDocument(VehicleDocumentType documentType) =>
        documentType is VehicleDocumentType.Registration
            or VehicleDocumentType.Inspection
            or VehicleDocumentType.Insurance
            or VehicleDocumentType.RoadFee;

    private async Task<VehicleDocument?> FindDuplicateNumberAsync(
        string documentNumber,
        int? excludeDocumentId,
        CancellationToken cancellationToken)
    {
        var normalizedNumber = documentNumber.Trim().ToUpper();

        return await _dbContext.VehicleDocuments
            .AsNoTracking()
            .Include(document => document.Vehicle)
            .FirstOrDefaultAsync(
                document =>
                    (!excludeDocumentId.HasValue || document.VehicleDocumentId != excludeDocumentId.Value) &&
                    document.DocumentNumber.ToUpper() == normalizedNumber,
                cancellationToken);
    }

    public async Task<IReadOnlyList<VehicleDocumentDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await Query()
            .ToListAsync(cancellationToken);

        return documents
            .OrderBy(item => item.DaysUntilExpiry ?? int.MaxValue)
            .ThenBy(item => item.VehicleName)
            .ToList();
    }

    public async Task<IReadOnlyList<VehicleDocumentDto>> GetByVehicleAsync(
    int vehicleId,
    CancellationToken cancellationToken = default)
    {
        var today = DateTime.Today;

        return await _dbContext.VehicleDocuments
            .AsNoTracking()
            .Where(document => document.VehicleId == vehicleId)

            .OrderBy(document => document.DocumentType)
            .ThenByDescending(document => document.IssuedDate)

            .Select(document => new VehicleDocumentDto(
                document.VehicleDocumentId,
                document.VehicleId,
                document.Vehicle.VehicleName,
                document.Vehicle.LicensePlate,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate,
                document.ImagePath,
                document.Notes,

                document.ExpiryDate.HasValue &&
                document.ExpiryDate.Value.Date < today,

                document.ExpiryDate.HasValue
                    ? EF.Functions.DateDiffDay(
                        today,
                        document.ExpiryDate.Value)
                    : null))

            .ToListAsync(cancellationToken);
    }


    public async Task<VehicleDocumentDto?> GetByIdAsync(
        int documentId,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.Today;

        return await _dbContext.VehicleDocuments
            .AsNoTracking()
            .Where(document => document.VehicleDocumentId == documentId)
            .Select(document => new VehicleDocumentDto(
                document.VehicleDocumentId,
                document.VehicleId,
                document.Vehicle.VehicleName,
                document.Vehicle.LicensePlate,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate,
                document.ImagePath,
                document.Notes,
                document.ExpiryDate.HasValue && document.ExpiryDate.Value.Date < today,
                document.ExpiryDate.HasValue
                    ? EF.Functions.DateDiffDay(today, document.ExpiryDate.Value)
                    : null))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<VehicleDocumentOverviewDto> GetOverviewByVehicleAsync(
        int vehicleId,
        CancellationToken cancellationToken = default)
    {
        var documents = await GetByVehicleAsync(vehicleId, cancellationToken);
        return BuildOverview(vehicleId, documents);
    }

    public async Task<VehicleLegalStatusDto> GetRentalLegalStatusAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        CancellationToken cancellationToken = default)
    {
        if (pickupDate >= returnDate)
        {
            return new VehicleLegalStatusDto(
                false,
                new[] { "Ngày trả xe phải sau ngày nhận xe." });
        }

        var documents = await GetByVehicleAsync(vehicleId, cancellationToken);
        return BuildRentalLegalStatus(documents, pickupDate.Date, returnDate.Date);
    }

    public async Task<IReadOnlyDictionary<int, VehicleDocumentOverviewDto>> GetOverviewsByVehicleIdsAsync(
        IReadOnlyCollection<int> vehicleIds,
        CancellationToken cancellationToken = default)
    {
        var ids = vehicleIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
        {
            return new Dictionary<int, VehicleDocumentOverviewDto>();
        }

        var requiredTypes = new[]
        {
            VehicleDocumentType.Registration,
            VehicleDocumentType.Inspection,
            VehicleDocumentType.Insurance,
            VehicleDocumentType.RoadFee
        };

        var today = DateTime.Today;
        var documents = await _dbContext.VehicleDocuments
            .AsNoTracking()
            .Where(document =>
                ids.Contains(document.VehicleId) &&
                requiredTypes.Contains(document.DocumentType))
            .OrderBy(document => document.VehicleId)
            .ThenBy(document => document.DocumentType)
            .ThenByDescending(document => document.IssuedDate)
            .Select(document => new VehicleDocumentDto(
                document.VehicleDocumentId,
                document.VehicleId,
                document.Vehicle.VehicleName,
                document.Vehicle.LicensePlate,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate,
                document.ImagePath,
                document.Notes,
                document.ExpiryDate.HasValue && document.ExpiryDate.Value.Date < today,
                document.ExpiryDate.HasValue
                    ? EF.Functions.DateDiffDay(today, document.ExpiryDate.Value)
                    : null))
            .ToListAsync(cancellationToken);

        var byVehicle = documents
            .GroupBy(document => document.VehicleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VehicleDocumentDto>)group.ToList());

        return ids.ToDictionary(
            vehicleId => vehicleId,
            vehicleId => BuildOverview(
                vehicleId,
                byVehicle.TryGetValue(vehicleId, out var vehicleDocuments)
                    ? vehicleDocuments
                    : Array.Empty<VehicleDocumentDto>()));
    }

    public async Task<OperationResult> CreateAsync(
        SaveVehicleDocumentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentNumber))
        {
            return OperationResult.Failure("Số giấy tờ không được để trống.");
        }

        if (request.IssuedDate.Date > DateTime.Today)
        {
            return OperationResult.Failure("Ngày cấp giấy tờ không được ở tương lai.");
        }

        if (request.ExpiryDate.HasValue && request.ExpiryDate.Value.Date <= request.IssuedDate.Date)
        {
            return OperationResult.Failure("Ngày hết hạn phải sau ngày cấp.");
        }

        var vehicle = await _dbContext.Vehicles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        var normalizedDocumentNumber = request.DocumentNumber.Trim();
        var duplicateNumber = await FindDuplicateNumberAsync(
            normalizedDocumentNumber,
            excludeDocumentId: null,
            cancellationToken);

        if (duplicateNumber is not null)
        {
            return OperationResult.Failure(
    $"Số giấy tờ '{normalizedDocumentNumber}' đã tồn tại trong hệ thống và đang thuộc xe {duplicateNumber.Vehicle.LicensePlate}. Vui lòng kiểm tra lại số giấy tờ.");
        }

        if (RequiresSingleActiveDocument(request.DocumentType))
        {
            var duplicateActiveDocument = await _dbContext.VehicleDocuments
                .AsNoTracking()
                .Where(document =>
                    document.VehicleId == request.VehicleId &&
                    document.DocumentType == request.DocumentType &&
                    (!document.ExpiryDate.HasValue || document.ExpiryDate.Value.Date >= DateTime.Today))
                .OrderByDescending(document => document.ExpiryDate ?? DateTime.MaxValue)
                .ThenByDescending(document => document.IssuedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (duplicateActiveDocument is not null)
            {
                var expiryText = duplicateActiveDocument.ExpiryDate.HasValue
                    ? $" Hết hạn: {duplicateActiveDocument.ExpiryDate.Value:dd/MM/yyyy}."
                    : " Không có ngày hết hạn.";

                return OperationResult.Failure(
                    $"Xe đã có {ToVietnameseName(request.DocumentType)} đang còn hiệu lực (số {duplicateActiveDocument.DocumentNumber}).{expiryText} Vui lòng sửa giấy tờ hiện tại thay vì thêm bản ghi trùng loại.");
            }
        }

        var document = new VehicleDocument
        {
            VehicleId = request.VehicleId,
            DocumentType = request.DocumentType,
            DocumentNumber = normalizedDocumentNumber,
            IssuedDate = request.IssuedDate,
            ExpiryDate = request.ExpiryDate,
            ImagePath = Normalize(request.ImagePath),
            Notes = Normalize(request.Notes),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.VehicleDocuments.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Create",
            nameof(VehicleDocument),
            document.VehicleDocumentId.ToString(),
            $"Thêm {ToVietnameseName(document.DocumentType)} cho xe {vehicle.LicensePlate}.",
            newValues: JsonSerializer.Serialize(new
            {
                document.VehicleId,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }


    public async Task<OperationResult> UpdateAsync(
        UpdateVehicleDocumentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentNumber))
        {
            return OperationResult.Failure("Số giấy tờ không được để trống.");
        }

        if (request.IssuedDate.Date > DateTime.Today)
        {
            return OperationResult.Failure("Ngày cấp giấy tờ không được ở tương lai.");
        }

        if (request.ExpiryDate.HasValue && request.ExpiryDate.Value.Date <= request.IssuedDate.Date)
        {
            return OperationResult.Failure("Ngày hết hạn phải sau ngày cấp.");
        }

        var document = await _dbContext.VehicleDocuments
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(
                item => item.VehicleDocumentId == request.VehicleDocumentId,
                cancellationToken);

        if (document is null)
        {
            return OperationResult.Failure("Không tìm thấy giấy tờ xe.");
        }

        if (document.VehicleId != request.VehicleId)
        {
            return OperationResult.Failure("Giấy tờ không thuộc xe đã chọn.");
        }

        var normalizedDocumentNumber = request.DocumentNumber.Trim();
        var duplicateNumber = await FindDuplicateNumberAsync(
            normalizedDocumentNumber,
            request.VehicleDocumentId,
            cancellationToken);

        if (duplicateNumber is not null)
        {
            return OperationResult.Failure(
    $"Số giấy tờ '{normalizedDocumentNumber}' đã tồn tại trong hệ thống và đang thuộc xe {duplicateNumber.Vehicle.LicensePlate}. Vui lòng kiểm tra lại số giấy tờ.");
        }

        if (RequiresSingleActiveDocument(request.DocumentType))
        {
            var duplicateActiveDocument = await _dbContext.VehicleDocuments
                .AsNoTracking()
                .Where(item =>
                    item.VehicleDocumentId != request.VehicleDocumentId &&
                    item.VehicleId == request.VehicleId &&
                    item.DocumentType == request.DocumentType &&
                    (!item.ExpiryDate.HasValue || item.ExpiryDate.Value.Date >= DateTime.Today))
                .OrderByDescending(item => item.ExpiryDate ?? DateTime.MaxValue)
                .ThenByDescending(item => item.IssuedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (duplicateActiveDocument is not null)
            {
                var expiryText = duplicateActiveDocument.ExpiryDate.HasValue
                    ? $" Hết hạn: {duplicateActiveDocument.ExpiryDate.Value:dd/MM/yyyy}."
                    : " Không có ngày hết hạn.";

                return OperationResult.Failure(
                    $"Xe đã có {ToVietnameseName(request.DocumentType)} khác đang còn hiệu lực (số {duplicateActiveDocument.DocumentNumber}).{expiryText} Vui lòng sửa giấy tờ đang hiệu lực đó trước khi cập nhật.");
            }
        }

        var oldValues = JsonSerializer.Serialize(new
        {
            document.VehicleId,
            document.DocumentType,
            document.DocumentNumber,
            document.IssuedDate,
            document.ExpiryDate,
            document.ImagePath,
            document.Notes
        });

        document.DocumentType = request.DocumentType;
        document.DocumentNumber = normalizedDocumentNumber;
        document.IssuedDate = request.IssuedDate;
        document.ExpiryDate = request.ExpiryDate;
        document.ImagePath = Normalize(request.ImagePath);
        document.Notes = Normalize(request.Notes);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Update",
            nameof(VehicleDocument),
            document.VehicleDocumentId.ToString(),
            $"Cập nhật {ToVietnameseName(document.DocumentType)} của xe {document.Vehicle.LicensePlate}.",
            oldValues: oldValues,
            newValues: JsonSerializer.Serialize(new
            {
                document.VehicleId,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate,
                document.ImagePath,
                document.Notes
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAsync(
        int documentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.VehicleDocuments
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.VehicleDocumentId == documentId, cancellationToken);

        if (document is null)
        {
            return OperationResult.Failure("Không tìm thấy giấy tờ xe.");
        }

        var oldValues = JsonSerializer.Serialize(new
        {
            document.VehicleId,
            document.DocumentType,
            document.DocumentNumber,
            document.IssuedDate,
            document.ExpiryDate
        });

        _dbContext.VehicleDocuments.Remove(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Delete",
            nameof(VehicleDocument),
            documentId.ToString(),
            $"Xóa {ToVietnameseName(document.DocumentType)} của xe {document.Vehicle.LicensePlate}.",
            oldValues: oldValues,
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private IQueryable<VehicleDocumentDto> Query()
    {
        var today = DateTime.Today;

        return _dbContext.VehicleDocuments
            .AsNoTracking()
            .Select(document => new VehicleDocumentDto(
                document.VehicleDocumentId,
                document.VehicleId,
                document.Vehicle.VehicleName,
                document.Vehicle.LicensePlate,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate,
                document.ImagePath,
                document.Notes,
                document.ExpiryDate.HasValue && document.ExpiryDate.Value < today,
                document.ExpiryDate.HasValue
                    ? EF.Functions.DateDiffDay(today, document.ExpiryDate.Value)
                    : null));
    }

    private static VehicleDocumentOverviewDto BuildOverview(
        int vehicleId,
        IReadOnlyCollection<VehicleDocumentDto> documents)
    {
        // Khi có nhiều bản ghi cùng loại giấy tờ (vd 2 lần mua Bảo hiểm), không được chọn đại diện
        // chỉ dựa vào "ngày cấp mới nhất" — vì rất có thể bản cấp gần đây lại hết hạn sớm hơn,
        // trong khi một bản cấp trước đó vẫn còn hiệu lực. Quy tắc chọn đúng thứ tự ưu tiên:
        //   1) Còn hiệu lực (chưa hết hạn) được ưu tiên hơn đã hết hạn.
        //   2) Trong nhóm còn hiệu lực: chọn bản có hạn dùng xa nhất (bao phủ tốt nhất);
        //      "không thời hạn" (ExpiryDate = null) được coi là xa nhất.
        //   3) Nếu vẫn hòa: chọn bản có ngày cấp mới nhất, rồi đến Id lớn nhất.
        VehicleDocumentDto? Latest(VehicleDocumentType type) => documents
            .Where(document => document.DocumentType == type)
            .OrderBy(document => document.IsExpired ? 1 : 0)
            .ThenByDescending(document => document.ExpiryDate ?? DateTime.MaxValue)
            .ThenByDescending(document => document.IssuedDate)
            .ThenByDescending(document => document.VehicleDocumentId)
            .FirstOrDefault();

        var registration = Latest(VehicleDocumentType.Registration);
        var inspection = Latest(VehicleDocumentType.Inspection);
        var insurance = Latest(VehicleDocumentType.Insurance);
        var roadFee = Latest(VehicleDocumentType.RoadFee);

        return new VehicleDocumentOverviewDto(
            vehicleId,
            registration,
            inspection,
            insurance,
            roadFee,
            BuildLegalStatus(registration, inspection, insurance, roadFee));
    }

    private static VehicleLegalStatusDto BuildRentalLegalStatus(
        IReadOnlyCollection<VehicleDocumentDto> documents,
        DateTime pickupDate,
        DateTime returnDate)
    {
        var reasons = new List<string>();

        ValidateDocumentForRental(
            documents,
            VehicleDocumentType.Registration,
            "Đăng ký xe",
            expiryRequired: false,
            pickupDate,
            returnDate,
            reasons);

        ValidateDocumentForRental(
            documents,
            VehicleDocumentType.Inspection,
            "Đăng kiểm",
            expiryRequired: true,
            pickupDate,
            returnDate,
            reasons);

        ValidateDocumentForRental(
            documents,
            VehicleDocumentType.Insurance,
            "Bảo hiểm",
            expiryRequired: true,
            pickupDate,
            returnDate,
            reasons);

        ValidateDocumentForRental(
            documents,
            VehicleDocumentType.RoadFee,
            "Phí đường bộ",
            expiryRequired: true,
            pickupDate,
            returnDate,
            reasons);

        return new VehicleLegalStatusDto(reasons.Count == 0, reasons);
    }

    private static void ValidateDocumentForRental(
        IReadOnlyCollection<VehicleDocumentDto> documents,
        VehicleDocumentType documentType,
        string displayName,
        bool expiryRequired,
        DateTime pickupDate,
        DateTime returnDate,
        ICollection<string> reasons)
    {
        var matching = documents
            .Where(document => document.DocumentType == documentType)
            .ToList();

        if (matching.Count == 0)
        {
            reasons.Add($"Thiếu giấy {displayName}.");
            return;
        }

        var valid = matching.Any(document =>
            document.IssuedDate.Date <= pickupDate &&
            ((!expiryRequired && !document.ExpiryDate.HasValue) ||
             (document.ExpiryDate.HasValue && document.ExpiryDate.Value.Date >= returnDate)));

        if (valid)
        {
            return;
        }

        var effectiveByPickup = matching
            .Where(document => document.IssuedDate.Date <= pickupDate)
            .ToList();

        if (effectiveByPickup.Count == 0)
        {
            var earliest = matching.OrderBy(document => document.IssuedDate).First();
            reasons.Add(
                $"{displayName} chưa có hiệu lực vào ngày nhận xe {pickupDate:dd/MM/yyyy} " +
                $"(ngày cấp {earliest.IssuedDate:dd/MM/yyyy}).");
            return;
        }

        if (expiryRequired && effectiveByPickup.All(document => !document.ExpiryDate.HasValue))
        {
            reasons.Add($"{displayName} chưa có ngày hết hạn để xác định hiệu lực đến ngày trả xe.");
            return;
        }

        var latestExpiry = effectiveByPickup
            .Where(document => document.ExpiryDate.HasValue)
            .Select(document => document.ExpiryDate!.Value.Date)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        if (latestExpiry != DateTime.MinValue)
        {
            reasons.Add(
                $"{displayName} hết hạn ngày {latestExpiry:dd/MM/yyyy}, " +
                $"trước ngày trả xe {returnDate:dd/MM/yyyy}.");
            return;
        }

        reasons.Add($"{displayName} không đủ hiệu lực cho khoảng thuê đã chọn.");
    }

    private static VehicleLegalStatusDto BuildLegalStatus(
        VehicleDocumentDto? registration,
        VehicleDocumentDto? inspection,
        VehicleDocumentDto? insurance,
        VehicleDocumentDto? roadFee)
    {
        var today = DateTime.Today;
        var reasons = new List<string>();

        ValidateDocument(
            registration,
            "Đăng ký xe",
            expiryRequired: false,
            today,
            reasons);

        ValidateDocument(
            inspection,
            "Đăng kiểm",
            expiryRequired: true,
            today,
            reasons);

        ValidateDocument(
            insurance,
            "Bảo hiểm",
            expiryRequired: true,
            today,
            reasons);

        ValidateDocument(
            roadFee,
            "Phí đường bộ",
            expiryRequired: true,
            today,
            reasons);

        return new VehicleLegalStatusDto(
            reasons.Count == 0,
            reasons);
    }

    private static void ValidateDocument(
        VehicleDocumentDto? document,
        string displayName,
        bool expiryRequired,
        DateTime today,
        ICollection<string> reasons)
    {
        if (document is null)
        {
            reasons.Add($"Thiếu giấy {displayName}.");
            return;
        }

        if (document.IssuedDate.Date > today)
        {
            reasons.Add(
                $"{displayName} chưa có hiệu lực vì ngày cấp là {document.IssuedDate:dd/MM/yyyy}.");
            return;
        }

        if (!document.ExpiryDate.HasValue)
        {
            if (expiryRequired)
            {
                reasons.Add($"{displayName} chưa có ngày hết hạn để xác định hiệu lực.");
            }

            return;
        }

        if (document.ExpiryDate.Value.Date < today)
        {
            reasons.Add(
                $"{displayName} hết hạn ngày {document.ExpiryDate.Value:dd/MM/yyyy}.");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
