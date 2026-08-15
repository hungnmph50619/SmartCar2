using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class HandoverService : IHandoverService
{
    private const string CustomerCheckInAction = "CustomerVehicleCheckInCompleted";

    private static readonly HashSet<string> FuelGaugeLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "8/8 (100%)",
        "7/8 (~87.5%)",
        "6/8 (75%)",
        "5/8 (~62.5%)",
        "4/8 (50%)",
        "3/8 (~37.5%)",
        "2/8 (25%)",
        "1/8 (~12.5%)",
        "0/8 (Gần cạn)"
    };

    private readonly ApplicationDbContext _dbContext;

    public HandoverService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<HandoverVehicleContextDto?> GetVehicleContextAsync(
        int bookingId,
        CancellationToken cancellationToken = default) =>
        _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.BookingId == bookingId)
            .Select(booking => new HandoverVehicleContextDto(
                booking.Vehicle.CurrentMileage,
                booking.Vehicle.FuelType))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<CustomerHandoverSnapshotDto?> GetCustomerSnapshotAsync(
        int bookingId,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking?.Handover is null)
        {
            return null;
        }

        var customerName = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == customerId)
            .Select(user => user.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Khách thuê";

        return new CustomerHandoverSnapshotDto(
            booking.BookingId,
            customerName,
            booking.Vehicle.VehicleName,
            booking.Vehicle.LicensePlate,
            booking.PickupDate,
            booking.ReturnDate,
            booking.Handover.HandoverAt,
            booking.Handover.Mileage,
            booking.Handover.FuelLevel,
            booking.Handover.Accessories,
            booking.Handover.Notes,
            SplitPaths(booking.Handover.ImagePaths),
            booking.Status == BookingStatus.ReadyForPickup);
    }

    public async Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            return OperationResult.Failure("Đơn chưa ở trạng thái sẵn sàng giao xe.");
        }

        if (booking.Handover is not null)
        {
            return OperationResult.Failure("Đơn đã có hồ sơ bàn giao và đang chờ khách check-in tình trạng xe.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);
        var depositPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid);

        if (!rentalPaid || !depositPaid)
        {
            return OperationResult.Failure(
                "Chỉ được lập hồ sơ bàn giao sau khi SmartCar đã xác nhận đủ tiền thuê và cọc bảo đảm trong giao dịch thanh toán ban đầu.");
        }

        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            return OperationResult.Failure("Xe hiện không ở trạng thái sẵn sàng.");
        }

        if (request.HandoverAt < booking.PickupDate)
        {
            return OperationResult.Failure(
                "Thời gian giao xe không được trước thời gian nhận xe đã đặt.");
        }

        if (request.HandoverAt >= booking.ReturnDate)
        {
            return OperationResult.Failure(
                "Thời gian giao xe phải trước thời gian trả xe đã đặt.");
        }

        if (request.Mileage < booking.Vehicle.CurrentMileage)
        {
            return OperationResult.Failure(
                $"ODO khi giao không được nhỏ hơn số km hiện tại của xe ({booking.Vehicle.CurrentMileage:N0} km).");
        }

        var fuelLevelResult = NormalizeFuelLevel(
            booking.Vehicle.FuelType,
            request.FuelLevel);
        if (!fuelLevelResult.Succeeded)
        {
            return OperationResult.Failure(fuelLevelResult.Error!);
        }

        booking.Handover = new VehicleHandover
        {
            HandoverAt = request.HandoverAt,
            Mileage = request.Mileage,
            FuelLevel = fuelLevelResult.Value!,
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            Accessories = Normalize(request.Accessories),
            ImagePaths = Normalize(request.ImagePaths),
            Notes = Normalize(request.Notes)
        };

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Cần check-in tình trạng xe trước khi nhận",
            Message =
                $"SmartCar đã lập hồ sơ bàn giao cho đơn #{booking.BookingId}. " +
                "Trước khi nhận chìa khóa, bạn phải tự chụp bộ ảnh check-in theo 6 góc bắt buộc trên tài khoản của mình và khai hư hỏng có sẵn nếu có."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmCustomerCheckInAsync(
        ConfirmCustomerHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId) ||
            string.IsNullOrWhiteSpace(request.SnapshotHash) ||
            string.IsNullOrWhiteSpace(request.CustomerImagePaths) ||
            string.IsNullOrWhiteSpace(request.EvidenceHash))
        {
            return OperationResult.Failure("Thiếu dữ liệu check-in tình trạng xe.");
        }

        var customerImagePaths = SplitPaths(request.CustomerImagePaths);
        if (customerImagePaths.Count < 6)
        {
            return OperationResult.Failure("Khách phải chụp đủ tối thiểu 6 ảnh check-in bắt buộc trước khi bắt đầu chuyến.");
        }

        if (customerImagePaths.Count > 12)
        {
            return OperationResult.Failure("Bộ ảnh check-in của khách tối đa 12 ảnh.");
        }

        var customerNote = Normalize(request.CustomerNote);
        if (request.HasPreExistingIssue && string.IsNullOrWhiteSpace(customerNote))
        {
            return OperationResult.Failure("Nếu phát hiện hư hỏng có sẵn, khách phải mô tả rõ vị trí/tình trạng trước khi nhận xe.");
        }

        if (customerNote?.Length > 1000)
        {
            return OperationResult.Failure("Ghi chú check-in của khách tối đa 1000 ký tự.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item =>
                item.BookingId == request.BookingId &&
                item.CustomerId == request.CustomerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (booking.Handover is null)
        {
            return OperationResult.Failure("SmartCar chưa lập hồ sơ bàn giao cho đơn này.");
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            return OperationResult.Failure(
                booking.Status == BookingStatus.Rented
                    ? "Bạn đã hoàn tất check-in và chuyến thuê đã bắt đầu."
                    : "Đơn không còn ở trạng thái chờ check-in nhận xe.");
        }

        var alreadyCheckedIn = await _dbContext.AuditLogs.AnyAsync(log =>
            log.Action == CustomerCheckInAction &&
            log.EntityId == booking.BookingId.ToString(),
            cancellationToken);
        if (alreadyCheckedIn)
        {
            return OperationResult.Failure("Bộ bằng chứng check-in của khách đã được khóa trước đó.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);
        var depositPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid);

        if (!rentalPaid || !depositPaid)
        {
            return OperationResult.Failure(
                "Không thể kích hoạt chuyến thuê vì tiền thuê hoặc cọc bảo đảm chưa ở trạng thái đã thanh toán.");
        }

        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            return OperationResult.Failure("Xe không còn ở trạng thái sẵn sàng để bàn giao.");
        }

        var checkedInAt = DateTime.UtcNow;
        var normalizedUserAgent = Normalize(request.UserAgent);
        if (normalizedUserAgent?.Length > 500)
        {
            normalizedUserAgent = normalizedUserAgent[..500];
        }

        booking.Status = BookingStatus.Rented;
        booking.Vehicle.Status = VehicleStatus.Rented;
        booking.Vehicle.CurrentMileage = booking.Handover.Mileage;

        var checkInEvidence = JsonSerializer.Serialize(new
        {
            request.BookingId,
            Mileage = booking.Handover.Mileage,
            FuelLevel = booking.Handover.FuelLevel,
            HasIssue = request.HasPreExistingIssue,
            Note = customerNote,
            ImagePaths = string.Join(';', customerImagePaths),
            AdminSnapshotHash = request.SnapshotHash.Trim(),
            EvidenceHash = request.EvidenceHash.Trim(),
            CheckedInAt = checkedInAt,
            UserAgent = normalizedUserAgent
        });

        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = request.CustomerId,
            Action = CustomerCheckInAction,
            EntityName = nameof(VehicleHandover),
            EntityId = booking.BookingId.ToString(),
            Description =
                $"Khách đã tự thực hiện check-in tình trạng xe đơn #{booking.BookingId} bằng {customerImagePaths.Count} ảnh; " +
                $"ODO bàn giao {booking.Handover.Mileage:N0} km; nhiên liệu/pin {booking.Handover.FuelLevel}; " +
                $"hư hỏng có sẵn do khách khai: {(request.HasPreExistingIssue ? "Có" : "Không")}; " +
                $"evidence SHA-256 {request.EvidenceHash.Trim()}. Booking chuyển sang Rented trong cùng giao dịch dữ liệu.",
            NewValues = checkInEvidence,
            IpAddress = Normalize(request.IpAddress),
            CreatedAt = checkedInAt
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Check-in hoàn tất · chuyến thuê đã bắt đầu",
            Message =
                $"Bộ ảnh check-in của đơn #{booking.BookingId} đã được khóa cùng thời gian và mã kiểm tra dữ liệu. " +
                "Khi trả xe, bạn sẽ thực hiện check-out bằng cùng các góc để SmartCar đối chiếu trước–sau."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static (bool Succeeded, string? Value, string? Error) NormalizeFuelLevel(
        string vehicleFuelType,
        string? rawFuelLevel)
    {
        if (string.IsNullOrWhiteSpace(rawFuelLevel))
        {
            return (false, null, "Vui lòng ghi nhận mức nhiên liệu khi giao xe.");
        }

        var value = rawFuelLevel.Trim();
        if (string.Equals(vehicleFuelType, "Điện", StringComparison.OrdinalIgnoreCase))
        {
            var numericValue = value.TrimEnd('%').Trim();
            if (!int.TryParse(numericValue, out var batteryPercent) ||
                batteryPercent < 0 || batteryPercent > 100)
            {
                return (false, null, "Mức pin xe điện phải từ 0% đến 100%.");
            }

            return (true, $"{batteryPercent}%", null);
        }

        if (!FuelGaugeLevels.Contains(value))
        {
            return (false, null,
                "Mức nhiên liệu xe xăng/dầu/hybrid phải được ghi theo vạch 0/8 đến 8/8 trên đồng hồ.");
        }

        return (true, value, null);
    }

    private static IReadOnlyList<string> SplitPaths(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
