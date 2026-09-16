using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Incidents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class PolicyAwareIncidentService : IIncidentService
{
    private readonly IncidentService _inner;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public PolicyAwareIncidentService(
        IncidentService inner,
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _inner = inner;
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public Task<IReadOnlyList<IncidentDto>> GetAllAsync(
        IncidentStatus? status = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetAllAsync(status, cancellationToken);

    public Task<OperationResult> StartInvestigationAsync(
        int incidentId,
        string adminId,
        CancellationToken cancellationToken = default) =>
        _inner.StartInvestigationAsync(incidentId, adminId, cancellationToken);

    public async Task<OperationResult> CreateAsync(
        CreateIncidentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        if (request.IncidentType != IncidentType.TrafficFine)
        {
            return await _inner.CreateAsync(request, adminId, cancellationToken);
        }

        if (!request.BookingId.HasValue)
        {
            return OperationResult.Failure(
                "Phạt nguội phải gắn với đúng đơn thuê để xác định người điều khiển.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return OperationResult.Failure("Vui lòng nhập nội dung thông báo vi phạm.");
        }

        if (request.FineAmount <= 0 || request.CustomerLiabilityAmount <= 0)
        {
            return OperationResult.Failure(
                "Phạt nguội phải có số tiền phạt và số tiền khách chịu lớn hơn 0.");
        }

        if (request.CustomerLiabilityAmount > request.FineAmount)
        {
            return OperationResult.Failure(
                "Số tiền khách chịu không được lớn hơn số tiền phạt chính thức.");
        }

        if (string.IsNullOrWhiteSpace(request.EvidencePaths))
        {
            return OperationResult.Failure(
                "Phải lưu đường dẫn ảnh/thông báo chính thức làm bằng chứng trước khi tạo khoản phải thu.");
        }

        if (request.OccurredAt > DateTime.Now.AddMinutes(5))
        {
            return OperationResult.Failure(
                "Thời điểm vi phạm không được ở tương lai.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item =>
                item.BookingId == request.BookingId.Value &&
                item.VehicleId == request.VehicleId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Đơn thuê không thuộc xe đã chọn.");
        }

        if (booking.Handover is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure(
                "Chỉ ghi nhận phạt nguội sau khi chuyến đã có thời gian giao và trả xe thực tế. " +
                "Không dùng PickupDate/ReturnDate dự kiến để quy trách nhiệm vi phạm.");
        }

        var actualStart = booking.Handover.HandoverAt;
        var actualEnd = booking.VehicleReturn.ReturnedAt;

        if (actualEnd < actualStart)
        {
            return OperationResult.Failure(
                "Dữ liệu giao/trả thực tế của chuyến không hợp lệ. Vui lòng kiểm tra biên bản trước khi ghi nhận phạt.");
        }

        if (request.OccurredAt < actualStart || request.OccurredAt > actualEnd)
        {
            return OperationResult.Failure(
                $"Thời điểm vi phạm {request.OccurredAt:dd/MM/yyyy HH:mm} không nằm trong khoảng khách thực tế giữ xe " +
                $"({actualStart:dd/MM/yyyy HH:mm} - {actualEnd:dd/MM/yyyy HH:mm}).");
        }

        var incident = new VehicleIncident
        {
            VehicleId = request.VehicleId,
            BookingId = booking.BookingId,
            IncidentType = IncidentType.TrafficFine,
            Status = IncidentStatus.Open,
            OccurredAt = request.OccurredAt,
            Location = Normalize(request.Location),
            Description = request.Description.Trim(),
            EstimatedCost = 0m,
            ActualCost = 0m,
            FineAmount = request.FineAmount,
            CustomerLiabilityAmount = request.CustomerLiabilityAmount,
            EvidencePaths = Normalize(request.EvidencePaths),
            Notes = AppendText(
                Normalize(request.Notes),
                "Khoản phải thu được tạo từ thông báo vi phạm. Nếu cọc của booking vẫn đang trong thời gian giữ theo policy đã chụp thì chưa được duyệt hoàn khi khoản phạt này còn mở; nếu cọc đã hoàn, khoản phải thu vẫn tồn tại độc lập và chặn chuyến mới cho đến khi được xử lý."),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.VehicleIncidents.Add(incident);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _dbContext.Payments.Add(new Payment
        {
            BookingId = booking.BookingId,
            VehicleIncidentId = incident.VehicleIncidentId,
            Type = PaymentType.TrafficFine,
            Amount = request.CustomerLiabilityAmount,
            Method = PaymentMethods.NotSelected,
            Status = PaymentStatus.Pending
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Có khoản phạt/vi phạm cần xử lý",
            Message =
                $"Đơn #{booking.BookingId} có thông báo vi phạm lúc {request.OccurredAt:dd/MM/yyyy HH:mm}. " +
                $"Nghĩa vụ hiện tại: {request.CustomerLiabilityAmount:N0} đồng. " +
                "Vui lòng kiểm tra bằng chứng và thanh toán. Tài khoản sẽ tạm không tạo chuyến thuê mới cho đến khi khoản này được xử lý."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "CreateTrafficFineReceivable",
            nameof(VehicleIncident),
            incident.VehicleIncidentId.ToString(),
            $"Ghi nhận phạt/vi phạm cho đơn #{booking.BookingId}: phạt {request.FineAmount:N0} đồng, khách chịu {request.CustomerLiabilityAmount:N0} đồng.",
            newValues: JsonSerializer.Serialize(new
            {
                incident.VehicleIncidentId,
                incident.BookingId,
                incident.VehicleId,
                incident.OccurredAt,
                incident.FineAmount,
                incident.CustomerLiabilityAmount,
                ActualRentalStart = actualStart,
                ActualRentalEnd = actualEnd
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ResolveAsync(
        ResolveIncidentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var incidentType = await _dbContext.VehicleIncidents
            .AsNoTracking()
            .Where(item => item.VehicleIncidentId == request.IncidentId)
            .Select(item => (IncidentType?)item.IncidentType)
            .FirstOrDefaultAsync(cancellationToken);

        if (incidentType != IncidentType.TrafficFine)
        {
            return await _inner.ResolveAsync(request, adminId, cancellationToken);
        }

        if (request.FineAmount <= 0 || request.CustomerLiabilityAmount <= 0)
        {
            return OperationResult.Failure(
                "Số tiền phạt và phần khách chịu phải lớn hơn 0.");
        }

        if (request.CustomerLiabilityAmount > request.FineAmount)
        {
            return OperationResult.Failure(
                "Số tiền khách chịu không được lớn hơn số tiền phạt chính thức.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var incident = await _dbContext.VehicleIncidents
            .Include(item => item.Vehicle)
            .Include(item => item.Booking)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.VehicleIncidentId == request.IncidentId, cancellationToken);

        if (incident is null)
        {
            return OperationResult.Failure("Không tìm thấy hồ sơ vi phạm.");
        }

        if (incident.Status is IncidentStatus.Resolved or IncidentStatus.Cancelled)
        {
            return OperationResult.Failure("Hồ sơ vi phạm đã kết thúc xử lý.");
        }

        var receivables = incident.Payments
            .Where(payment => payment.Type == PaymentType.TrafficFine)
            .OrderBy(payment => payment.PaymentId)
            .ToList();
        var paidTotal = receivables
            .Where(payment => payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var awaitingTotal = receivables
            .Where(payment => payment.Status == PaymentStatus.AwaitingConfirmation)
            .Sum(payment => payment.Amount);

        if (paidTotal > request.CustomerLiabilityAmount)
        {
            return OperationResult.Failure(
                "Khách đã thanh toán nhiều hơn nghĩa vụ mới nhập. Không được giảm nghĩa vụ trực tiếp; hãy xử lý hoàn tiền/điều chỉnh có kiểm soát.");
        }

        if (awaitingTotal > 0 && paidTotal + awaitingTotal != request.CustomerLiabilityAmount)
        {
            return OperationResult.Failure(
                "Khách đang có khoản chuyển phạt chờ đối soát. Không được đổi số tiền trong lúc đang xác nhận giao dịch.");
        }

        var targetOutstanding = Math.Max(0m, request.CustomerLiabilityAmount - paidTotal - awaitingTotal);
        var editable = receivables
            .Where(payment => payment.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            .ToList();

        if (editable.Count > 0)
        {
            var primary = editable[0];
            primary.Amount = targetOutstanding;
            primary.Method = PaymentMethods.NotSelected;
            primary.Status = targetOutstanding > 0 ? PaymentStatus.Pending : PaymentStatus.Failed;
            primary.PaidAt = null;
            primary.TransactionCode = null;

            foreach (var duplicate in editable.Skip(1))
            {
                _dbContext.Payments.Remove(duplicate);
            }
        }
        else if (targetOutstanding > 0)
        {
            _dbContext.Payments.Add(new Payment
            {
                BookingId = incident.BookingId!.Value,
                VehicleIncidentId = incident.VehicleIncidentId,
                Type = PaymentType.TrafficFine,
                Amount = targetOutstanding,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending
            });
        }

        incident.FineAmount = request.FineAmount;
        incident.CustomerLiabilityAmount = request.CustomerLiabilityAmount;
        incident.ActualCost = 0m;
        incident.Notes = Normalize(request.Notes) ?? incident.Notes;
        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = DateTime.UtcNow;

        if (incident.Booking is not null)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = incident.Booking.CustomerId,
                Title = targetOutstanding > 0 || awaitingTotal > 0
                    ? "Đã chốt nghĩa vụ vi phạm"
                    : "Hồ sơ vi phạm đã hoàn tất",
                Message = targetOutstanding > 0
                    ? $"Đơn #{incident.BookingId}: nghĩa vụ vi phạm đã chốt {request.CustomerLiabilityAmount:N0} đồng; còn {targetOutstanding:N0} đồng cần thanh toán."
                    : awaitingTotal > 0
                        ? $"Đơn #{incident.BookingId}: nghĩa vụ đã chốt {request.CustomerLiabilityAmount:N0} đồng và giao dịch đang chờ SmartCar đối soát."
                        : $"Đơn #{incident.BookingId}: nghĩa vụ vi phạm {request.CustomerLiabilityAmount:N0} đồng đã được thanh toán đầy đủ."
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ResolveTrafficFineReceivable",
            nameof(VehicleIncident),
            incident.VehicleIncidentId.ToString(),
            $"Chốt phạt {request.FineAmount:N0} đồng; khách chịu {request.CustomerLiabilityAmount:N0} đồng; còn phải thu {targetOutstanding:N0} đồng.",
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
