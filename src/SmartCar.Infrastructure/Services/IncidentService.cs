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
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return OperationResult.Failure("Vui lòng nhập mô tả sự cố.");
        }

        if (request.EstimatedCost < 0 || request.FineAmount < 0 || request.CustomerLiabilityAmount < 0)
        {
            return OperationResult.Failure("Các khoản tiền không được âm.");
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
                .FirstOrDefaultAsync(item =>
                    item.BookingId == request.BookingId.Value &&
                    item.VehicleId == request.VehicleId,
                    cancellationToken);

            if (relatedBooking is null)
            {
                return OperationResult.Failure("Đơn thuê không thuộc xe đã chọn.");
            }

            if (request.IncidentType == IncidentType.TrafficFine &&
                (request.OccurredAt < relatedBooking.PickupDate || request.OccurredAt > relatedBooking.ReturnDate))
            {
                return OperationResult.Failure(
                    "Thời điểm vi phạm không nằm trong thời gian của đơn thuê đã chọn. Hãy kiểm tra lại đơn hoặc thời điểm vi phạm.");
            }
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
            Description = request.Description.Trim(),
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

        if (request.IncidentType != IncidentType.TrafficFine && vehicle.Status != VehicleStatus.Rented)
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
        if (request.ActualCost < 0 || request.FineAmount < 0 || request.CustomerLiabilityAmount < 0)
        {
            return OperationResult.Failure("Các khoản tiền không được âm.");
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
        incident.ActualCost = customerHandlesTrafficFine
            ? 0m
            : request.RequiresMaintenance ? 0m : request.ActualCost;
        incident.FineAmount = customerHandlesTrafficFine ? 0m : request.FineAmount;
        incident.CustomerLiabilityAmount = customerHandlesTrafficFine ? 0m : request.CustomerLiabilityAmount;
        incident.Notes = Normalize(request.Notes) ?? incident.Notes;
        incident.ResolvedAt = DateTime.UtcNow;

        if (!customerHandlesTrafficFine && request.RequiresMaintenance)
        {
            incident.Vehicle.Status = VehicleStatus.Maintenance;

            var hasOpenMaintenance = await _dbContext.MaintenanceRecords.AnyAsync(item =>
                item.VehicleId == incident.VehicleId &&
                item.Status == MaintenanceStatus.InProgress,
                cancellationToken);

            if (!hasOpenMaintenance)
            {
                _dbContext.MaintenanceRecords.Add(new MaintenanceRecord
                {
                    VehicleId = incident.VehicleId,
                    StartDate = DateTime.UtcNow,
                    Content = $"Khắc phục sự cố #{incident.VehicleIncidentId}: {incident.Description}",
                    Cost = request.ActualCost,
                    Mileage = incident.Vehicle.CurrentMileage,
                    Status = MaintenanceStatus.InProgress
                });
            }
        }
        else
        {
            incident.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
                _dbContext,
                incident.Vehicle,
                excludedIncidentId: incident.VehicleIncidentId,
                cancellationToken: cancellationToken);
        }

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
                RequiresMaintenance = customerHandlesTrafficFine ? false : request.RequiresMaintenance,
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
