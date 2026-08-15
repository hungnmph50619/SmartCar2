using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ReturnService : IReturnService
{
    private const int LateGraceMinutes = 30;
    private const decimal HourlyLateRateFactor = 0.10m;
    private const decimal MaximumLateFeePerDayFactor = 1.50m;

    private const string ReturnReviewPendingAction = "ReturnEvidencePendingCustomerReview";
    private const string ReturnReviewAcceptedAction = "CustomerAcceptedReturnEvidence";
    private const string ReturnReviewDisputedAction = "CustomerDisputedReturnEvidence";
    private const string ReturnReviewResolvedAction = "AdminResolvedReturnEvidenceDispute";

    private static readonly string[] ReturnReviewActions =
    {
        ReturnReviewPendingAction,
        ReturnReviewAcceptedAction,
        ReturnReviewDisputedAction,
        ReturnReviewResolvedAction
    };

    private readonly ApplicationDbContext _dbContext;

    public ReturnService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ReturnPreparationDto?> GetPreparationAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId && item.Handover != null)
            .Select(item => new
            {
                item.BookingId,
                item.ReturnDate,
                ScheduledReturnLocation = item.ReturnLocation ?? string.Empty,
                HandoverAt = item.Handover!.HandoverAt,
                HandoverMileage = item.Handover.Mileage,
                HandoverFuelLevel = item.Handover.FuelLevel,
                HandoverAccessories = item.Handover.Accessories,
                HandoverImagePaths = item.Handover.ImagePaths,
                VehicleFuelType = item.Vehicle.FuelType
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return null;
        }

        var signedEvidenceJson = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == "CustomerSignedHandover" &&
                log.EntityName == nameof(VehicleHandover) &&
                log.EntityId == bookingId.ToString())
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => log.NewValues)
            .FirstOrDefaultAsync(cancellationToken);

        var (customerImagePaths, customerNote) = ParseCustomerHandoverEvidence(signedEvidenceJson);

        return new ReturnPreparationDto(
            booking.BookingId,
            booking.ReturnDate,
            booking.ScheduledReturnLocation,
            booking.HandoverAt,
            booking.HandoverMileage,
            booking.HandoverFuelLevel,
            booking.HandoverAccessories,
            booking.HandoverImagePaths,
            customerImagePaths,
            customerNote,
            booking.VehicleFuelType);
    }

    public async Task<OperationResult> CreateAsync(
        CreateReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.Rented || booking.Handover is null)
        {
            return OperationResult.Failure("Chỉ đơn đang thuê và đã bàn giao xe mới được trả xe.");
        }

        if (booking.VehicleReturn is not null)
        {
            return OperationResult.Failure("Đơn đã có biên bản trả xe.");
        }

        if (request.Mileage < booking.Handover.Mileage)
        {
            return OperationResult.Failure(
                $"ODO khi nhận lại ({request.Mileage:N0} km) không được nhỏ hơn ODO lúc giao ({booking.Handover.Mileage:N0} km).");
        }

        if (request.ReturnedAt < booking.Handover.HandoverAt)
        {
            return OperationResult.Failure("Thời gian trả xe không được trước thời gian giao xe.");
        }

        if (string.IsNullOrWhiteSpace(request.FuelLevel))
        {
            return OperationResult.Failure("Vui lòng ghi nhận mức nhiên liệu hoặc mức pin khi trả xe.");
        }

        if (string.IsNullOrWhiteSpace(request.ReturnLocation))
        {
            return OperationResult.Failure("Vui lòng ghi nhận địa điểm trả xe thực tế.");
        }

        var returnLocation = request.ReturnLocation.Trim();
        if (returnLocation.Length > 250)
        {
            return OperationResult.Failure("Địa điểm trả xe tối đa 250 ký tự.");
        }

        var isEarlyReturn = request.ReturnedAt < booking.ReturnDate;
        var lateMinutes = request.ReturnedAt > booking.ReturnDate
            ? (int)Math.Ceiling((request.ReturnedAt - booking.ReturnDate).TotalMinutes)
            : 0;

        var (chargeableLateMinutes, chargeableLateHours, lateFee) =
            CalculateLateFee(lateMinutes, booking.DailyPrice);

        var vehicleReturn = new VehicleReturn
        {
            ReturnedAt = request.ReturnedAt,
            ReturnLocation = returnLocation,
            Mileage = request.Mileage,
            FuelLevel = request.FuelLevel.Trim(),
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            HasDamage = request.HasDamage,
            IsLateReturn = lateMinutes > 0,
            LateMinutes = lateMinutes,
            LateFee = lateFee,
            ImagePaths = Normalize(request.ImagePaths),
            Notes = Normalize(request.Notes)
        };

        if (lateFee > 0)
        {
            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                ChargeType = AdditionalChargeType.LateReturn,
                Description =
                    $"Trả xe muộn {lateMinutes} phút. SmartCar miễn phí 30 phút đầu; " +
                    $"thời gian tính phí {chargeableLateMinutes} phút (~{chargeableLateHours} giờ), " +
                    $"đơn giá theo giờ bằng 10% giá thuê ngày và tối đa 150% giá thuê ngày cho mỗi 24 giờ.",
                Amount = lateFee
            });
        }

        booking.VehicleReturn = vehicleReturn;
        booking.Status = BookingStatus.PendingInspection;
        booking.Vehicle.Status = VehicleStatus.Inspection;
        booking.Vehicle.CurrentMileage = request.Mileage;

        var reviewCreatedAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = null,
            Action = ReturnReviewPendingAction,
            EntityName = nameof(VehicleReturn),
            EntityId = booking.BookingId.ToString(),
            Description =
                $"SmartCar đã tiếp nhận xe đơn #{booking.BookingId} và tạo bộ bằng chứng trả xe để khách đối chiếu với biên bản bàn giao. " +
                "Đơn chưa được hoàn tất cho đến khi khách xác nhận hiện trạng hoặc tranh chấp được xử lý.",
            NewValues = JsonSerializer.Serialize(new
            {
                booking.BookingId,
                request.ReturnedAt,
                ReturnLocation = returnLocation,
                request.Mileage,
                FuelLevel = request.FuelLevel.Trim(),
                request.HasDamage,
                ReturnCondition = Normalize(request.ExteriorCondition),
                ReturnImagePaths = Normalize(request.ImagePaths)
            }),
            CreatedAt = reviewCreatedAt
        });

        var reviewInstruction =
            " Mở chi tiết đơn để xem ảnh bàn giao ↔ ảnh trả xe và chọn Đồng ý hoặc Không đồng ý/Yêu cầu xem xét.";

        if (lateFee > 0)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Xe được ghi nhận trả muộn",
                Message =
                    $"Đơn #{booking.BookingId} trả muộn {lateMinutes} phút tại {returnLocation}. " +
                    $"Sau 30 phút ân hạn, phí trả muộn tạm tính là {lateFee:N0} đồng. " +
                    "Phụ phí cuối cùng sẽ được đối trừ với cọc bảo đảm trước khi yêu cầu khách thanh toán thêm." +
                    reviewInstruction
            });
        }
        else if (lateMinutes > 0)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Đã tiếp nhận xe trả",
                Message =
                    $"Đơn #{booking.BookingId} được tiếp nhận muộn {lateMinutes} phút tại {returnLocation}, " +
                    "vẫn nằm trong thời gian ân hạn 30 phút nên không phát sinh phí trả muộn. Xe đang chờ kiểm tra và quyết toán cọc." +
                    reviewInstruction
            });
        }
        else if (isEarlyReturn)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Đã tiếp nhận xe trả sớm",
                Message =
                    $"Đơn #{booking.BookingId} đã được SmartCar tiếp nhận xe lúc " +
                    $"{request.ReturnedAt:dd/MM/yyyy HH:mm} tại {returnLocation}. " +
                    "Xe đang chờ kiểm tra và quyết toán cọc bảo đảm." +
                    reviewInstruction
            });
        }
        else
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Đã tiếp nhận xe trả",
                Message =
                    $"Đơn #{booking.BookingId} đã được tiếp nhận xe tại {returnLocation}. " +
                    "Xe đang chờ kiểm tra và quyết toán cọc bảo đảm." +
                    reviewInstruction
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddChargeAsync(
        AddChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Description))
        {
            return OperationResult.Failure("Mô tả và số tiền phụ phí không hợp lệ.");
        }

        var booking = await ChargeQuery()
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure("Không tìm thấy biên bản trả xe.");
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Chỉ đơn đang chờ kiểm tra mới được thêm phụ phí.");
        }

        if (HasPaidAdditionalCharge(booking))
        {
            return OperationResult.Failure("Không thể sửa phụ phí sau khi khách đã thanh toán phần vượt cọc.");
        }

        booking.VehicleReturn.AdditionalCharges.Add(new AdditionalCharge
        {
            ChargeType = request.ChargeType,
            Description = request.Description.Trim(),
            Amount = request.Amount
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveChargeAsync(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken = default)
    {
        var booking = await ChargeQuery()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure("Không tìm thấy biên bản trả xe.");
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Chỉ đơn đang chờ kiểm tra mới được xóa phụ phí.");
        }

        if (HasPaidAdditionalCharge(booking))
        {
            return OperationResult.Failure("Không thể sửa phụ phí sau khi khách đã thanh toán phần vượt cọc.");
        }

        var charge = booking.VehicleReturn.AdditionalCharges
            .FirstOrDefault(item => item.AdditionalChargeId == additionalChargeId);

        if (charge is null)
        {
            return OperationResult.Failure("Không tìm thấy phụ phí.");
        }

        _dbContext.AdditionalCharges.Remove(charge);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CompleteAsync(
        int bookingId,
        bool requiresMaintenance,
        string? maintenanceNote,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn hoặc biên bản trả xe.");
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Đơn chưa ở trạng thái chờ hoàn tất kiểm tra.");
        }

        var latestReviewAction = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(VehicleReturn) &&
                log.EntityId == bookingId.ToString() &&
                ReturnReviewActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => log.Action)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestReviewAction == ReturnReviewPendingAction)
        {
            return OperationResult.Failure(
                "Khách chưa phản hồi bộ ảnh/hiện trạng trả xe. Hãy chờ khách xác nhận hoặc kiểm tra mục Đối chiếu & phản hồi khách trước khi hoàn tất đơn.");
        }

        if (latestReviewAction == ReturnReviewDisputedAction)
        {
            return OperationResult.Failure(
                "Khách đang không đồng ý với hiện trạng trả xe. Phải ghi nhận xử lý tranh chấp và căn cứ kết luận trước khi hoàn tất đơn hoặc quyết toán cọc.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);
        var extensionPaid = booking.Extensions.All(extension =>
            extension.Status != BookingExtensionStatus.Approved);

        var depositAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var depositApplied = Math.Min(depositAmount, Math.Max(0, booking.AdditionalAmount));
        var additionalDueAfterDeposit = Math.Max(0, booking.AdditionalAmount - depositApplied);
        var additionalPaidAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var additionalPaid =
            additionalDueAfterDeposit <= 0 ||
            additionalPaidAmount >= additionalDueAfterDeposit;

        if (!rentalPaid || !extensionPaid || !additionalPaid)
        {
            return OperationResult.Failure(
                additionalDueAfterDeposit > additionalPaidAmount
                    ? $"Sau khi đối trừ cọc, khách còn phải thanh toán {(additionalDueAfterDeposit - additionalPaidAmount):N0} đồng phụ phí trước khi hoàn tất đơn."
                    : "Đơn vẫn còn khoản tiền chưa được thanh toán.");
        }

        if (depositApplied > 0)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.AdditionalCharge,
                Amount = depositApplied,
                Method = RentalPolicyConstants.SecurityDepositSettlementMethod,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow,
                TransactionCode = $"DEPSET{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}"
            });
        }

        var depositRefundAmount = Math.Max(0, depositAmount - depositApplied);
        if (depositRefundAmount > 0 &&
            !booking.Payments.Any(payment =>
                payment.Type == PaymentType.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded))
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.DepositRefund,
                Amount = depositRefundAmount,
                Method = RentalPolicyConstants.SecurityDepositRefundMethod,
                Status = PaymentStatus.AwaitingRefund,
                PaidAt = null,
                TransactionCode = null
            });
        }

        booking.Status = BookingStatus.Completed;
        booking.Vehicle.Status = requiresMaintenance
            ? VehicleStatus.Maintenance
            : VehicleStatus.Available;

        if (requiresMaintenance)
        {
            _dbContext.MaintenanceRecords.Add(new MaintenanceRecord
            {
                VehicleId = booking.VehicleId,
                StartDate = DateTime.UtcNow,
                Content = string.IsNullOrWhiteSpace(maintenanceNote)
                    ? "Kiểm tra hoặc sửa chữa sau lượt thuê"
                    : maintenanceNote.Trim(),
                Cost = 0,
                Mileage = booking.Vehicle.CurrentMileage,
                Status = MaintenanceStatus.InProgress
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã hoàn tất và cọc đã được quyết toán",
            Message = depositAmount > 0
                ? depositRefundAmount > 0
                    ? $"Đơn #{booking.BookingId} đã hoàn tất. Cọc {depositAmount:N0} đồng được đối trừ {depositApplied:N0} đồng phụ phí; khoản hoàn cọc {depositRefundAmount:N0} đồng đã được tạo và đang chờ chuyển trả."
                    : $"Đơn #{booking.BookingId} đã hoàn tất. Toàn bộ cọc {depositAmount:N0} đồng đã được dùng để đối trừ phụ phí có căn cứ nên không còn số dư cọc phải hoàn."
                : $"Đơn #{booking.BookingId} đã hoàn tất. Bạn có thể đánh giá trải nghiệm thuê xe."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static (int ChargeableMinutes, int ChargeableHours, decimal Fee) CalculateLateFee(
        int lateMinutes,
        decimal dailyPrice)
    {
        if (lateMinutes <= LateGraceMinutes)
        {
            return (0, 0, 0);
        }

        var chargeableMinutes = lateMinutes - LateGraceMinutes;
        var fullDays = chargeableMinutes / 1440;
        var remainingMinutes = chargeableMinutes % 1440;
        var remainingHours = remainingMinutes > 0
            ? (int)Math.Ceiling(remainingMinutes / 60d)
            : 0;

        var hourlyRate = Math.Round(dailyPrice * HourlyLateRateFactor, 0);
        var maximumPerDay = Math.Round(dailyPrice * MaximumLateFeePerDayFactor, 0);
        var remainingFee = Math.Min(remainingHours * hourlyRate, maximumPerDay);
        var fee = fullDays * maximumPerDay + remainingFee;
        var totalChargeableHours = fullDays * 24 + remainingHours;

        return (chargeableMinutes, totalChargeableHours, fee);
    }

    private IQueryable<Booking> ChargeQuery() =>
        _dbContext.Bookings
            .Include(booking => booking.VehicleReturn)
                .ThenInclude(vehicleReturn => vehicleReturn!.AdditionalCharges)
            .Include(booking => booking.Payments);

    private async Task RecalculateChargesAsync(
        Booking booking,
        CancellationToken cancellationToken)
    {
        booking.AdditionalAmount = await _dbContext.AdditionalCharges
            .Where(charge => charge.VehicleReturn.BookingId == booking.BookingId)
            .SumAsync(charge => (decimal?)charge.Amount, cancellationToken) ?? 0;

        booking.TotalAmount = Math.Max(
            0,
            booking.RentalAmount + booking.DeliveryFee + booking.AdditionalAmount);

        var depositAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var amountDueAfterDeposit = Math.Max(0, booking.AdditionalAmount - depositAmount);

        var pendingPayment = booking.Payments.FirstOrDefault(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Status == PaymentStatus.Pending);

        if (amountDueAfterDeposit > 0)
        {
            if (pendingPayment is null)
            {
                booking.Payments.Add(new Payment
                {
                    Type = PaymentType.AdditionalCharge,
                    Amount = amountDueAfterDeposit,
                    Method = PaymentMethods.NotSelected,
                    Status = PaymentStatus.Pending
                });
            }
            else
            {
                pendingPayment.Amount = amountDueAfterDeposit;
            }
        }
        else if (pendingPayment is not null)
        {
            _dbContext.Payments.Remove(pendingPayment);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool HasPaidAdditionalCharge(Booking booking) =>
        booking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Status == PaymentStatus.Paid);

    private static (string? ImagePaths, string? Note) ParseCustomerHandoverEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var imagePaths = root.TryGetProperty("CustomerImagePaths", out var imagesElement) &&
                             imagesElement.ValueKind == JsonValueKind.String
                ? imagesElement.GetString()
                : null;
            var note = root.TryGetProperty("CustomerNote", out var noteElement) &&
                       noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()
                : null;
            return (Normalize(imagePaths), Normalize(note));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
