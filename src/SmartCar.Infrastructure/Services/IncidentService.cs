using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Incidents;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class IncidentService : IIncidentService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public IncidentService(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<IncidentDto>> GetAllAsync(
        IncidentStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.VehicleIncidents.AsNoTracking();
        if (status.HasValue)
        {
            query = query.Where(item => item.Status == status.Value);
        }

        return await query
            .OrderByDescending(item => item.OccurredAt)
            .Select(item => new IncidentDto(
                item.VehicleIncidentId,
                item.VehicleId,
                item.Vehicle.VehicleName,
                item.Vehicle.LicensePlate,
                item.BookingId,
                item.IncidentType,
                item.Status,
                item.OccurredAt,
                item.Location,
                item.Description,
                item.EstimatedCost,
                item.ActualCost,
                item.FineAmount,
                item.CustomerLiabilityAmount,
                item.EvidencePaths,
                item.Notes,
                item.ResolvedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> CreateAsync(
        CreateIncidentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var validationError =
            IncidentValidationRules.ValidateCreate(request);
        if (validationError is not null)
        {
            return OperationResult.Failure(validationError);
        }

        if (request.IncidentType == IncidentType.TrafficFine && !request.BookingId.HasValue)
        {
            return OperationResult.Failure("Vi phạm giao thông/phạt nguội phải gắn với đơn thuê để xác định người điều khiển.");
        }

        var vehicle = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        Booking? relatedBooking = null;
        if (request.BookingId.HasValue)
        {
            relatedBooking = await _dbContext.Bookings
                .Include(item => item.Handover)
                .Include(item => item.VehicleReturn)
                .FirstOrDefaultAsync(item =>
                    item.BookingId == request.BookingId.Value &&
                    item.VehicleId == request.VehicleId,
                    cancellationToken);

            if (relatedBooking is null)
            {
                return OperationResult.Failure("Đơn thuê không thuộc xe đã chọn.");
            }

            if (relatedBooking.Handover is null)
            {
                return OperationResult.Failure(
                    "Đơn liên quan chưa có thời gian giao xe thực tế. Không thể gắn sự cố vào đơn này.");
            }

            var actualStart = relatedBooking.Handover.HandoverAt;
            var actualEnd = relatedBooking.VehicleReturn?.ReturnedAt ?? DateTime.Now.AddMinutes(5);

            if (actualEnd < actualStart)
            {
                return OperationResult.Failure(
                    "Dữ liệu giao/trả thực tế của đơn không hợp lệ. Vui lòng kiểm tra biên bản trước khi ghi nhận sự cố.");
            }

            if (request.OccurredAt < actualStart || request.OccurredAt > actualEnd)
            {
                return OperationResult.Failure(
                    $"Thời điểm xảy ra {request.OccurredAt:dd/MM/yyyy HH:mm} không nằm trong khoảng khách thực tế giữ xe " +
                    $"({actualStart:dd/MM/yyyy HH:mm} - {(relatedBooking.VehicleReturn is null ? "hiện tại" : actualEnd.ToString("dd/MM/yyyy HH:mm"))}).");
            }
        }

        var normalizedDescription = request.Description.Trim();
        var isDuplicate = await _dbContext.VehicleIncidents
            .AsNoTracking()
            .AnyAsync(item =>
                item.VehicleId == request.VehicleId &&
                item.BookingId == request.BookingId &&
                item.IncidentType == request.IncidentType &&
                item.OccurredAt == request.OccurredAt &&
                item.Description == normalizedDescription &&
                item.Status != IncidentStatus.Cancelled,
                cancellationToken);

        if (isDuplicate)
        {
            return OperationResult.Failure(
                "Sự cố/vi phạm này đã được ghi nhận trước đó. Vui lòng kiểm tra danh sách để tránh tạo trùng.");
        }

        var customerHandlesTrafficFine = request.IncidentType == IncidentType.TrafficFine;
        var incident = new VehicleIncident
        {
            VehicleId = request.VehicleId,
            BookingId = request.BookingId,
            IncidentType = request.IncidentType,
            Status = IncidentStatus.Open,
            OccurredAt = request.OccurredAt,
            Location = Normalize(request.Location),
            Description = normalizedDescription,
            EstimatedCost = customerHandlesTrafficFine ? 0m : request.EstimatedCost,
            FineAmount = customerHandlesTrafficFine ? 0m : request.FineAmount,
            CustomerLiabilityAmount = customerHandlesTrafficFine ? 0m : request.CustomerLiabilityAmount,
            EvidencePaths = Normalize(request.EvidencePaths),
            Notes = customerHandlesTrafficFine
                ? AppendText(
                    Normalize(request.Notes),
                    "Khách trực tiếp làm việc với cơ quan có thẩm quyền; SmartCar chỉ cung cấp hồ sơ thuê xe để xác định người điều khiển.")
                : Normalize(request.Notes),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.VehicleIncidents.Add(incident);

        if (request.IncidentType != IncidentType.TrafficFine &&
            vehicle.Status is not (VehicleStatus.Rented or VehicleStatus.Inspection or VehicleStatus.Inactive))
        {
            vehicle.Status = VehicleStatus.Maintenance;
        }

        if (customerHandlesTrafficFine && relatedBooking is not null)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = relatedBooking.CustomerId,
                Title = "Thông báo vi phạm giao thông",
                Message =
                    $"Đơn #{relatedBooking.BookingId} có vi phạm ghi nhận lúc {request.OccurredAt:dd/MM/yyyy HH:mm}. " +
                    "Vui lòng trực tiếp làm việc với cơ quan có thẩm quyền khi được yêu cầu. SmartCar cung cấp hồ sơ thuê xe cần thiết để xác định người điều khiển."
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Create",
            nameof(VehicleIncident),
            incident.VehicleIncidentId.ToString(),
            $"Ghi nhận {incident.IncidentType} cho xe {vehicle.LicensePlate}.",
            newValues: JsonSerializer.Serialize(new
            {
                incident.VehicleId,
                incident.BookingId,
                incident.IncidentType,
                incident.OccurredAt,
                incident.EstimatedCost,
                incident.FineAmount,
                incident.CustomerLiabilityAmount,
                CustomerHandlesTrafficFine = customerHandlesTrafficFine
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> StartInvestigationAsync(
        int incidentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var incident = await _dbContext.VehicleIncidents
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.VehicleIncidentId == incidentId, cancellationToken);

        if (incident is null)
        {
            return OperationResult.Failure("Không tìm thấy sự cố.");
        }

        if (incident.Status != IncidentStatus.Open)
        {
            return OperationResult.Failure("Chỉ sự cố mới ghi nhận mới chuyển sang đang xử lý.");
        }

        incident.Status = IncidentStatus.Investigating;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "UpdateStatus",
            nameof(VehicleIncident),
            incidentId.ToString(),
            $"Chuyển sự cố xe {incident.Vehicle.LicensePlate} sang đang xử lý.",
            oldValues: IncidentStatus.Open.ToString(),
            newValues: IncidentStatus.Investigating.ToString(),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ResolveAsync(
        ResolveIncidentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var validationError =
            IncidentValidationRules.ValidateResolve(request);
        if (validationError is not null)
        {
            return OperationResult.Failure(validationError);
        }

        var incident = await _dbContext.VehicleIncidents
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.VehicleIncidentId == request.IncidentId, cancellationToken);

        if (incident is null)
        {
            return OperationResult.Failure("Không tìm thấy sự cố.");
        }

        if (incident.Status is IncidentStatus.Resolved or IncidentStatus.Cancelled)
        {
            return OperationResult.Failure("Sự cố đã kết thúc xử lý.");
        }

        var oldValues = JsonSerializer.Serialize(new
        {
            incident.Status,
            incident.ActualCost,
            incident.FineAmount,
            incident.CustomerLiabilityAmount
        });

        var customerHandlesTrafficFine = incident.IncidentType == IncidentType.TrafficFine;

        incident.Status = IncidentStatus.Resolved;
        incident.ActualCost = customerHandlesTrafficFine ? 0m : request.ActualCost;
        incident.FineAmount = customerHandlesTrafficFine ? 0m : request.FineAmount;
        incident.CustomerLiabilityAmount = customerHandlesTrafficFine ? 0m : request.CustomerLiabilityAmount;
        incident.Notes = Normalize(request.Notes) ?? incident.Notes;
        incident.ResolvedAt = DateTime.UtcNow;

        incident.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            incident.Vehicle,
            excludedIncidentId: incident.VehicleIncidentId,
            cancellationToken: cancellationToken);

        if (customerHandlesTrafficFine && incident.BookingId.HasValue)
        {
            var customerId = await _dbContext.Bookings
                .Where(item => item.BookingId == incident.BookingId.Value)
                .Select(item => item.CustomerId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                _dbContext.Notifications.Add(new Notification
                {
                    UserId = customerId,
                    Title = "Vi phạm giao thông đã cập nhật",
                    Message = $"SmartCar đã ghi nhận hồ sơ vi phạm của đơn #{incident.BookingId.Value} là đã hoàn tất xử lý."
                });
            }
        }
        else if (request.CustomerLiabilityAmount > 0 && incident.BookingId.HasValue)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = await _dbContext.Bookings
                    .Where(item => item.BookingId == incident.BookingId.Value)
                    .Select(item => item.CustomerId)
                    .FirstAsync(cancellationToken),
                Title = "Kết quả xử lý sự cố",
                Message = $"Sự cố #{incident.VehicleIncidentId} xác định phần trách nhiệm của khách là {request.CustomerLiabilityAmount:N0} đồng. Admin sẽ cập nhật phụ phí vào biên bản trả xe."
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Resolve",
            nameof(VehicleIncident),
            incident.VehicleIncidentId.ToString(),
            $"Hoàn tất xử lý sự cố xe {incident.Vehicle.LicensePlate}.",
            oldValues: oldValues,
            newValues: JsonSerializer.Serialize(new
            {
                incident.Status,
                incident.ActualCost,
                incident.FineAmount,
                incident.CustomerLiabilityAmount,
                incident.ResolvedAt,
                CustomerHandlesTrafficFine = customerHandlesTrafficFine
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";
}
