using System.Data;
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
    private const int MinimumEvidenceImages = 7;
    private const int MaximumEvidenceImages = 25;
    private const int MaximumReasonableKilometersPerDay = 2500;
    private const int MaximumChargeDescriptionLength = 250;
    private const string AccessoriesComplete = "Đủ";
    private const string AccessoriesMissingPrefix = "Thiếu/mất:";
    private const string ReturnAccessoriesLabel = "Phụ kiện khi trả:";
    private const string ReturnNoteSeparator = " | Ghi chú: ";
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly ApplicationDbContext _dbContext;

    public ReturnService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationResult> CreateAsync(
        CreateReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityVerifiedByStaffId))
        {
            return OperationResult.Failure(
                "Không xác định được nhân viên đã trực tiếp kiểm tra người trả xe.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
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
                $"Số km trả xe không được nhỏ hơn lúc giao ({booking.Handover.Mileage:N0} km).");
        }

        if (request.ReturnedAt < booking.Handover.HandoverAt)
        {
            return OperationResult.Failure("Thời gian trả xe không được trước thời gian giao xe.");
        }

        // Demo: allow a future return timestamp to exercise return/deposit scenarios.

        var evidencePaths = SplitImagePaths(request.ImagePaths)
            .Where(path => !path.Contains(ReturnSignedMarker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (evidencePaths.Length < MinimumEvidenceImages)
        {
            return OperationResult.Failure(
                $"Biên bản trả xe phải có ít nhất {MinimumEvidenceImages} ảnh đối chiếu bắt buộc.");
        }

        if (evidencePaths.Length > MaximumEvidenceImages)
        {
            return OperationResult.Failure(
                $"Biên bản trả xe chỉ được có tối đa {MaximumEvidenceImages} ảnh chứng cứ.");
        }

        if (request.HasDamage &&
            !evidencePaths.Any(path => path.Contains("damage-", StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Failure(
                "Đã ghi nhận hư hỏng mới thì biên bản trả xe phải có ít nhất một ảnh hư hỏng.");
        }

        var accessoryStatus = Normalize(request.InteriorCondition);
        if (accessoryStatus is null ||
            (accessoryStatus != AccessoriesComplete &&
             !accessoryStatus.StartsWith(AccessoriesMissingPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Failure("Vui lòng xác nhận phụ kiện khi trả là Đủ hoặc ghi rõ đồ thiếu/mất.");
        }

        if (accessoryStatus.StartsWith(AccessoriesMissingPrefix, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(accessoryStatus[AccessoriesMissingPrefix.Length..]))
        {
            return OperationResult.Failure("Vui lòng ghi rõ phụ kiện bị thiếu hoặc mất.");
        }

        var drivenKilometers = request.Mileage - booking.Handover.Mileage;
        var elapsedDays = Math.Max(
            1,
            (int)Math.Ceiling((request.ReturnedAt - booking.Handover.HandoverAt).TotalHours / 24d));
        var maximumReasonableKilometers = elapsedDays * MaximumReasonableKilometersPerDay;

        if (drivenKilometers > maximumReasonableKilometers)
        {
            return OperationResult.Failure(
                $"Số km tăng {drivenKilometers:N0} km trong {elapsedDays} ngày là bất thường. " +
                $"Vui lòng kiểm tra lại công-tơ-mét lúc giao ({booking.Handover.Mileage:N0} km) và lúc trả ({request.Mileage:N0} km) trước khi tiếp tục.");
        }

        if (!TryParseFuelPercent(request.FuelLevel, out var fuelPercent))
        {
            return OperationResult.Failure("Mức nhiên liệu khi trả phải là số từ 0 đến 100%.");
        }

        var lateMinutes = request.ReturnedAt > booking.ReturnDate
            ? (int)Math.Ceiling((request.ReturnedAt - booking.ReturnDate).TotalMinutes)
            : 0;
        var lateDays = lateMinutes > 0
            ? Math.Max(1, (int)Math.Ceiling(lateMinutes / 1440d))
            : 0;
        var lateReturnMultiplier = booking.Handover.LateReturnFeeMultiplier >= 1
            ? booking.Handover.LateReturnFeeMultiplier
            : RentalPolicy.LateReturnFeeMultiplier;
        var lateFee = lateDays * booking.DailyPrice * lateReturnMultiplier;

        var paidRentalDays = Math.Max(
            1,
            (int)Math.Ceiling((booking.ReturnDate - booking.PickupDate).TotalHours / 24d));
        var effectiveIncludedKilometers = Math.Max(
            booking.Handover.IncludedKilometers,
            paidRentalDays * RentalPolicy.IncludedKilometersPerDay);

        var identityVerifiedAt = DateTime.UtcNow;
        var vehicleReturn = new VehicleReturn
        {
            ReturnedAt = request.ReturnedAt,
            Mileage = request.Mileage,
            FuelLevel = $"{fuelPercent}%",
            ExteriorCondition = null,
            InteriorCondition = null,
            HasDamage = request.HasDamage,
            IsLateReturn = lateMinutes > 0,
            LateMinutes = lateMinutes,
            LateFee = lateFee,
            ImagePaths = string.Join(';', evidencePaths),
            Notes = BuildReturnNotes(accessoryStatus, request.Notes),
            CustomerIdentityVerified = true,
            IdentityVerifiedByStaffId = request.IdentityVerifiedByStaffId,
            IdentityVerifiedAt = identityVerifiedAt
        };

        var excessKilometers = Math.Max(0, drivenKilometers - effectiveIncludedKilometers);
        var excessMileageFee = excessKilometers * booking.Handover.ExcessKmFeePerKm;

        if (lateFee > 0)
        {
            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                ChargeType = AdditionalChargeType.LateReturn,
                Description =
                    $"Phí trả xe muộn {lateMinutes} phút " +
                    $"({lateDays} ngày tính phí x {lateReturnMultiplier:0.##}).",
                Amount = lateFee
            });
        }

        if (excessMileageFee > 0)
        {
            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                ChargeType = AdditionalChargeType.ExcessMileage,
                Description =
                    $"Phí vượt {excessKilometers:N0} km so với định mức " +
                    $"{effectiveIncludedKilometers:N0} km x {booking.Handover.ExcessKmFeePerKm:N0} đ/km.",
                Amount = excessMileageFee
            });
        }

        foreach (var extension in booking.Extensions.Where(extension =>
                     extension.Status is BookingExtensionStatus.Pending or BookingExtensionStatus.NeedsEvidence))
        {
            extension.Status = BookingExtensionStatus.Cancelled;
            extension.AdminNote = "Yêu cầu tự động đóng vì xe đã được trả.";
            extension.DecidedAt = DateTime.UtcNow;
        }

        booking.VehicleReturn = vehicleReturn;
        booking.Status = BookingStatus.PendingInspection;
        booking.Vehicle.Status = VehicleStatus.Inspection;
        booking.Vehicle.CurrentMileage = request.Mileage;

        if (lateFee > 0)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Xe được ghi nhận trả muộn",
                Message =
                    $"Đơn #{booking.BookingId} trả muộn {lateMinutes} phút. " +
                    $"Phí trả muộn tạm tính: {lateFee:N0} đồng."
            });
        }

        // Biên bản trả + kết quả đối chiếu đúng người được lưu cùng một transaction.
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddChargeAsync(
        AddChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.ChargeType))
        {
            return OperationResult.Failure("Loại phụ phí không hợp lệ.");
        }

        if (request.ChargeType is AdditionalChargeType.LateReturn or AdditionalChargeType.ExcessMileage)
        {
            return OperationResult.Failure(
                "Phí trả muộn và phí vượt km do hệ thống tự động tính, không được tạo thủ công.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(description))
        {
            return OperationResult.Failure("Cần mô tả căn cứ và số tiền phụ phí hợp lệ.");
        }

        if (description.Length > MaximumChargeDescriptionLength)
        {
            return OperationResult.Failure(
                $"Mô tả phụ phí tối đa {MaximumChargeDescriptionLength} ký tự.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

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

        if (!AllStaffChecksCompleted(booking))
        {
            return OperationResult.Failure(
                "Phải xác minh đúng người nhận/trả và bản ký giao/trả trước khi ghi nhận phụ phí.");
        }

        if (HasLockedAdditionalChargePayment(booking))
        {
            return OperationResult.Failure(
                "Không thể sửa phụ phí sau khi khách đã báo chuyển khoản hoặc khoản phụ phí đã được thanh toán.");
        }

        var returnEvidence = SplitImagePaths(booking.VehicleReturn.ImagePaths)
            .Where(path => !path.Contains(ReturnSignedMarker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (returnEvidence.Length < MinimumEvidenceImages)
        {
            return OperationResult.Failure("Không đủ ảnh trả xe làm căn cứ. Chưa thể tạo phụ phí.");
        }

        if (request.ChargeType == AdditionalChargeType.Damage)
        {
            if (!booking.VehicleReturn.HasDamage)
            {
                return OperationResult.Failure("Cần ghi nhận hư hỏng trong biên bản trả xe trước khi tạo phí hư hỏng.");
            }

            if (!returnEvidence.Any(path => path.Contains("damage-", StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Failure("Cần ảnh hư hỏng trong biên bản trả xe trước khi tạo phí hư hỏng.");
            }
        }

        if (request.ChargeType == AdditionalChargeType.MissingAccessory)
        {
            if (string.IsNullOrWhiteSpace(booking.Handover?.Accessories))
            {
                return OperationResult.Failure("Biên bản giao xe chưa ghi phụ kiện ban đầu nên chưa đủ căn cứ tạo phí thiếu phụ kiện.");
            }

            if (!HasMissingAccessories(booking.VehicleReturn.Notes))
            {
                return OperationResult.Failure("Biên bản trả xe chưa ghi nhận phụ kiện thiếu/mất nên chưa thể tạo phí này.");
            }
        }

        booking.VehicleReturn.AdditionalCharges.Add(new AdditionalCharge
        {
            ChargeType = request.ChargeType,
            Description = description,
            Amount = request.Amount
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveChargeAsync(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

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

        if (!AllStaffChecksCompleted(booking))
        {
            return OperationResult.Failure(
                "Phải xác minh đúng người nhận/trả và bản ký giao/trả trước khi thay đổi phụ phí.");
        }

        if (HasLockedAdditionalChargePayment(booking))
        {
            return OperationResult.Failure(
                "Không thể sửa phụ phí sau khi khách đã báo chuyển khoản hoặc khoản phụ phí đã được thanh toán.");
        }

        var charge = booking.VehicleReturn.AdditionalCharges
            .FirstOrDefault(item => item.AdditionalChargeId == additionalChargeId);

        if (charge is null)
        {
            return OperationResult.Failure("Không tìm thấy phụ phí.");
        }

        if (charge.ChargeType is AdditionalChargeType.LateReturn or AdditionalChargeType.ExcessMileage)
        {
            return OperationResult.Failure(
                "Phí trả muộn và phí vượt km do hệ thống tự động tính nên không thể xóa thủ công.");
        }

        _dbContext.AdditionalCharges.Remove(charge);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CompleteAsync(
        int bookingId,
        bool requiresMaintenance,
        string? maintenanceNote,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
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

        if (!AllStaffChecksCompleted(booking))
        {
            return OperationResult.Failure(
                "Chưa đủ hồ sơ: phải xác minh đúng người nhận/trả và bản ký giao/trả trước khi quyết toán.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);

        var depositPaid = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var depositAlreadyRefundedOrPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var depositAlreadyDeducted = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method == PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var depositSatisfied = booking.DepositAmount <= 0 ||
            Math.Max(0m, depositPaid - depositAlreadyRefundedOrPlanned) >= booking.DepositAmount;

        var extensionPaid = booking.Extensions.All(extension =>
            extension.Status != BookingExtensionStatus.Approved);

        var cashAdditionalPaid = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var additionalPaid = booking.AdditionalAmount <= 0 || cashAdditionalPaid >= booking.AdditionalAmount;

        var hasOpenAdditionalPayment = booking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Method != PaymentMethods.DepositDeduction &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation);

        var swapAdjustmentPaid = booking.Payments.All(payment =>
            payment.Type != PaymentType.VehicleSwapAdjustment ||
            payment.Status == PaymentStatus.Paid);

        if (!rentalPaid ||
            !depositSatisfied ||
            !extensionPaid ||
            !additionalPaid ||
            hasOpenAdditionalPayment ||
            !swapAdjustmentPaid)
        {
            return OperationResult.Failure(
                "Đơn vẫn còn khoản tiền chưa thanh toán, đang chờ đối soát hoặc chưa ghi nhận đủ tiền cọc.");
        }

        foreach (var extension in booking.Extensions.Where(extension =>
                     extension.Status is BookingExtensionStatus.Pending or BookingExtensionStatus.NeedsEvidence))
        {
            extension.Status = BookingExtensionStatus.Cancelled;
            extension.AdminNote = "Yêu cầu tự động đóng vì chuyến thuê đã kết thúc.";
            extension.DecidedAt = DateTime.UtcNow;
        }

        var depositAvailableBeforeNewDeduction = Math.Max(
            0m,
            depositPaid - depositAlreadyRefundedOrPlanned - depositAlreadyDeducted);
        var reservedCompensation = booking.Extensions.Sum(extension =>
            CompensationLedger.SumReservedAmount(extension.CustomerNote));
        var compensationNotYetApplied = Math.Max(0m, reservedCompensation - depositAlreadyDeducted);
        var newDepositDeduction = Math.Min(
            depositAvailableBeforeNewDeduction,
            compensationNotYetApplied);

        if (newDepositDeduction > 0)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.AdditionalCharge,
                Amount = newDepositDeduction,
                Method = PaymentMethods.DepositDeduction,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow,
                TransactionCode = $"EXT-COMP-{booking.BookingId}-{DateTime.UtcNow:yyyyMMddHHmmss}"
            });
        }

        var totalDepositDeducted = depositAlreadyDeducted + newDepositDeduction;
        var depositToRefund = Math.Max(
            0m,
            depositAvailableBeforeNewDeduction - newDepositDeduction);

        if (depositToRefund > 0)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = depositToRefund,
                Method = PaymentMethods.DepositRefund,
                Status = PaymentStatus.AwaitingRefund
            });

            booking.RefundAmount += depositToRefund;
        }

        if (totalDepositDeducted > 0)
        {
            booking.RefundReason = AppendText(
                booking.RefundReason,
                $"Cọc còn giữ trước quyết toán: {depositAvailableBeforeNewDeduction:N0} đồng. " +
                $"Khấu trừ bồi thường: {newDepositDeduction:N0} đồng. " +
                $"Cọc còn hoàn: {depositToRefund:N0} đồng.");
        }
        else if (depositToRefund > 0)
        {
            booking.RefundReason = AppendText(
                booking.RefundReason,
                $"Hoàn cọc còn lại sau khi kiểm tra xe: {depositToRefund:N0} đồng.");
        }

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

        var hasPendingRefund = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Refund &&
            payment.Status == PaymentStatus.AwaitingRefund);
        booking.Status = hasPendingRefund
            ? BookingStatus.AwaitingRefund
            : BookingStatus.Completed;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = hasPendingRefund ? "Đã kiểm tra xe - chờ hoàn tiền" : "Đơn thuê đã hoàn tất",
            Message = hasPendingRefund
                ? totalDepositDeducted > 0
                    ? $"Đơn #{booking.BookingId} đã kiểm tra xong. Đã khấu trừ {totalDepositDeducted:N0} đồng từ cọc; còn {depositToRefund:N0} đồng đang chờ hoàn."
                    : $"Đơn #{booking.BookingId} đã kiểm tra xong. Cọc còn lại {depositToRefund:N0} đồng đang chờ hoàn."
                : $"Đơn #{booking.BookingId} đã hoàn tất. Bạn có thể đánh giá chuyến thuê."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private IQueryable<Booking> ChargeQuery() =>
        _dbContext.Bookings
            .Include(booking => booking.Handover)
            .Include(booking => booking.VehicleReturn)
            .ThenInclude(vehicleReturn => vehicleReturn!.AdditionalCharges)
            .Include(booking => booking.Payments);

    private async Task RecalculateChargesAsync(
        Booking booking,
        CancellationToken cancellationToken)
    {
        var storedDeliveryFee = Math.Max(
            0m,
            booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);

        booking.AdditionalAmount = await _dbContext.AdditionalCharges
            .Where(charge => charge.VehicleReturn.BookingId == booking.BookingId)
            .SumAsync(charge => (decimal?)charge.Amount, cancellationToken)
            ?? 0;

        booking.TotalAmount = Math.Max(
            0,
            booking.RentalAmount + storedDeliveryFee + booking.AdditionalAmount);

        var pendingPayments = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Pending)
            .OrderBy(payment => payment.PaymentId)
            .ToList();

        var pendingPayment = pendingPayments.FirstOrDefault();
        foreach (var duplicate in pendingPayments.Skip(1))
        {
            _dbContext.Payments.Remove(duplicate);
        }

        if (booking.AdditionalAmount > 0)
        {
            if (pendingPayment is null)
            {
                booking.Payments.Add(new Payment
                {
                    Type = PaymentType.AdditionalCharge,
                    Amount = booking.AdditionalAmount,
                    Method = PaymentMethods.NotSelected,
                    Status = PaymentStatus.Pending
                });
            }
            else
            {
                pendingPayment.Amount = booking.AdditionalAmount;
            }
        }
        else if (pendingPayment is not null)
        {
            _dbContext.Payments.Remove(pendingPayment);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool AllStaffChecksCompleted(Booking booking) =>
        booking.Handover is not null &&
        booking.VehicleReturn is not null &&
        booking.Handover.CustomerIdentityVerified &&
        booking.Handover.SignedDocumentVerified &&
        booking.VehicleReturn.CustomerIdentityVerified &&
        booking.VehicleReturn.SignedDocumentVerified &&
        HasSignedCopy(booking.Handover.ImagePaths, HandoverSignedMarker) &&
        HasSignedCopy(booking.VehicleReturn.ImagePaths, ReturnSignedMarker);

    private static bool HasSignedCopy(string? imagePaths, string marker) =>
        SplitImagePaths(imagePaths)
            .Any(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool HasLockedAdditionalChargePayment(Booking booking) =>
        booking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Method != PaymentMethods.DepositDeduction &&
            payment.Status is PaymentStatus.AwaitingConfirmation or PaymentStatus.Paid);

    private static bool HasMissingAccessories(string? notes) =>
        !string.IsNullOrWhiteSpace(notes) &&
        notes.TrimStart().StartsWith(
            $"{ReturnAccessoriesLabel} {AccessoriesMissingPrefix}",
            StringComparison.OrdinalIgnoreCase);

    private static string BuildReturnNotes(string accessoryStatus, string? note)
    {
        var normalizedNote = Normalize(note);
        return normalizedNote is null
            ? $"{ReturnAccessoriesLabel} {accessoryStatus}"
            : $"{ReturnAccessoriesLabel} {accessoryStatus}{ReturnNoteSeparator}{normalizedNote}";
    }

    private static IReadOnlyList<string> SplitImagePaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    private static bool TryParseFuelPercent(string? value, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith('%'))
        {
            normalized = normalized[..^1].Trim();
        }

        return int.TryParse(normalized, out percent) && percent >= 0 && percent <= 100;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";
}

