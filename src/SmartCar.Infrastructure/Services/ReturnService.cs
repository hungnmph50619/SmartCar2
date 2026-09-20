using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Operations;
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

        if (request.IdentityFaceSessionId == Guid.Empty)
        {
            return OperationResult.Failure(
                "Cần chụp ảnh khuôn mặt người trả xe trực tiếp trước khi lập biên bản.");
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

        var extensionPayments = booking.Payments
            .Where(payment => payment.Type == PaymentType.Extension)
            .ToList();

        if (extensionPayments.Any(payment =>
                BookingWorkflowRules.BlocksVehicleReturnForExtensionPayment(payment.Status)))
        {
            return OperationResult.Failure(
                "Đơn đang có tiền gia hạn chuyển khoản/QR chờ Staff đối soát. " +
                "Cần xác nhận hoặc từ chối giao dịch trước khi nhận xe trả để tránh thất lạc tiền khách đã chuyển.");
        }

        var paidExtensionAmount = extensionPayments
            .Where(payment => payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var effectivePaidExtensionAmount = booking.Extensions
            .Where(extension => extension.Status == BookingExtensionStatus.Paid)
            .Sum(extension => extension.AdditionalAmount);

        if (!BookingWorkflowRules.IsExtensionPaymentLedgerConsistent(
                paidExtensionAmount,
                effectivePaidExtensionAmount))
        {
            return OperationResult.Failure(
                "Dữ liệu gia hạn đang lệch giữa tiền đã thu và yêu cầu gia hạn có hiệu lực. " +
                "Vui lòng đối soát dữ liệu gia hạn trước khi lập biên bản trả xe.");
        }

        var faceSession = await _dbContext.Set<IdentityCaptureSession>()
            .FirstOrDefaultAsync(item =>
                item.IdentityCaptureSessionId == request.IdentityFaceSessionId,
                cancellationToken);

        if (faceSession is null ||
            faceSession.Purpose != IdentityCapturePurposes.Return ||
            faceSession.BookingId != booking.BookingId ||
            faceSession.TargetCustomerId != booking.CustomerId ||
            !faceSession.CanConsume(DateTime.UtcNow) ||
            !IdentityCaptureMethods.All.Contains(faceSession.CaptureMethod ?? string.Empty, StringComparer.Ordinal))
        {
            return OperationResult.Failure(
                "Ảnh mặt người trả không hợp lệ, không thuộc đúng đơn/khách hoặc đã được dùng. Vui lòng chụp lại tại quầy.");
        }

        if (request.Mileage < booking.Handover.Mileage)
        {
            return OperationResult.Failure(
                $"Số km trả xe không được nhỏ hơn lúc giao ({booking.Handover.Mileage:N0} km).");
        }

        // ReturnedAt là thời điểm nghiệp vụ thực tế, vì vậy lấy từ server khi Staff lưu biên bản.
        // Không tin một timestamp tùy ý từ trình duyệt để tránh tính sai phí trả muộn.
        var actualReturnedAt = DateTime.Now;
        // TEST Quy_2: tạm bỏ validation thứ tự thời gian giao/trả để chạy hết chuyến.

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

        var accessoryStatus = Normalize(request.AccessoryStatus);
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
            (int)Math.Ceiling((actualReturnedAt - booking.Handover.HandoverAt).TotalHours / 24d));
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

        var lateMinutes = actualReturnedAt > booking.ReturnDate
            ? (int)Math.Ceiling((actualReturnedAt - booking.ReturnDate).TotalMinutes)
            : 0;
        var lateDays = booking.Policy.LateChargeDays(lateMinutes);
        var lateReturnMultiplier = booking.Handover.LateReturnFeeMultiplier >= 1
            ? booking.Handover.LateReturnFeeMultiplier
            : RentalPolicy.LateReturnFeeMultiplier;
        var lateFee = lateDays * booking.DailyPrice * lateReturnMultiplier;

        var paidRentalDays = Math.Max(
            1,
            (int)Math.Ceiling((booking.ReturnDate - booking.PickupDate).TotalHours / 24d));
        var effectiveIncludedKilometers = Math.Max(
            booking.Handover.IncludedKilometers,
            paidRentalDays * booking.Policy.IncludedKilometersPerDay);

        var identityVerifiedAt = DateTime.UtcNow;
        var vehicleReturn = new VehicleReturn
        {
            ReturnedAt = actualReturnedAt,
            Mileage = request.Mileage,
            FuelLevel = $"{fuelPercent}%",
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            AccessoryStatus = accessoryStatus,
            HasDamage = request.HasDamage,
            IsLateReturn = lateMinutes > 0,
            LateMinutes = lateMinutes,
            LateFee = lateFee,
            ImagePaths = string.Join(';', evidencePaths),
            ReturnerFaceImagePath = faceSession.ImagePath,
            ReturnerFaceCapturedAt = faceSession.CompletedAt,
            ReturnerFaceCaptureMethod = faceSession.CaptureMethod,
            Notes = BuildReturnNotes(accessoryStatus, request.Notes),
            CustomerIdentityVerified = true,
            IdentityVerifiedByStaffId = request.IdentityVerifiedByStaffId,
            IdentityVerifiedAt = identityVerifiedAt
        };

        faceSession.ConsumedAt = identityVerifiedAt;

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
                     BookingWorkflowRules.ShouldCancelExtensionWhenVehicleReturns(extension.Status)))
        {
            var previousStatus = extension.Status;
            extension.Status = BookingExtensionStatus.Cancelled;
            extension.AdminNote = previousStatus == BookingExtensionStatus.Approved
                ? "Gia hạn đã được duyệt nhưng chưa thanh toán nên chưa có hiệu lực; tự động đóng vì xe đã được trả."
                : "Yêu cầu tự động đóng vì xe đã được trả.";
            extension.DecidedAt = DateTime.UtcNow;
        }

        foreach (var payment in extensionPayments.Where(payment =>
                     payment.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Failed;
            payment.Method = PaymentMethods.NotSelected;
            payment.PaidAt = null;
            payment.TransactionCode = null;
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

        if (request.ChargeType == AdditionalChargeType.Fuel)
        {
            if (!TryParseFuelPercent(booking.Handover?.FuelLevel, out var handoverFuelPercent) ||
                !TryParseFuelPercent(booking.VehicleReturn.FuelLevel, out var returnFuelPercent))
            {
                return OperationResult.Failure(
                    "Không đọc được mức nhiên liệu giao/trả nên chưa đủ căn cứ tạo phí nhiên liệu.");
            }

            if (returnFuelPercent >= handoverFuelPercent)
            {
                return OperationResult.Failure(
                    "Nhiên liệu khi trả không thấp hơn lúc giao nên không được tạo phí nhiên liệu.");
            }

            if (booking.VehicleReturn.AdditionalCharges.Any(charge =>
                    charge.ChargeType == AdditionalChargeType.Fuel))
            {
                return OperationResult.Failure(
                    "Đơn đã có một khoản phí nhiên liệu. Hãy xóa khoản cũ trước khi ghi lại.");
            }
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

            if (!HasMissingAccessories(booking.VehicleReturn))
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

    public async Task<OperationResult> FinalizeChargesAsync(
        int bookingId,
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
            return OperationResult.Failure("Chỉ đơn đang chờ kiểm tra mới được chốt phụ phí.");
        }

        if (!AllStaffChecksCompleted(booking))
        {
            return OperationResult.Failure(
                "Phải xác minh đúng người nhận/trả và bản ký giao/trả trước khi chốt phụ phí.");
        }

        if (booking.AdditionalAmount <= 0m)
        {
            return OperationResult.Failure("Đơn không có phụ phí cần chốt.");
        }

        if (booking.Payments.Any(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status is PaymentStatus.AwaitingConfirmation or PaymentStatus.Paid))
        {
            return OperationResult.Failure(
                "Phụ phí đã bắt đầu thanh toán hoặc đã thanh toán nên không thể chốt lại.");
        }

        if (booking.Payments.Any(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Pending &&
                AdditionalChargeSettlementPolicy.IsReadyMarker(payment.TransactionCode)))
        {
            return OperationResult.Failure(
                "Phụ phí đã được chốt. Nếu cần sửa, hãy mở lại phụ phí trước.");
        }

        var pendingPayments = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Pending)
            .OrderBy(payment => payment.PaymentId)
            .ToList();

        var payment = pendingPayments.FirstOrDefault();
        if (payment is null)
        {
            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.AdditionalCharge
            };
            booking.Payments.Add(payment);
        }

        var now = DateTime.UtcNow;
        payment.Amount = booking.AdditionalAmount;
        payment.Method = PaymentMethods.NotSelected;
        payment.Status = PaymentStatus.Pending;
        payment.PaidAt = null;
        payment.TransactionCode =
            AdditionalChargeSettlementPolicy.CreateReadyMarker(booking.BookingId, now);

        foreach (var duplicate in pendingPayments.Where(item => item != payment))
        {
            _dbContext.Payments.Remove(duplicate);
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã chốt phụ phí sau đối chiếu xe",
            Message =
                $"Đơn #{booking.BookingId}: SmartCar đã chốt tổng phụ phí {booking.AdditionalAmount:N0} đồng sau khi đối chiếu biên bản giao - trả. Bạn có thể thanh toán khoản này."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ReopenChargesAsync(
        int bookingId,
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
            return OperationResult.Failure("Đơn không còn ở bước kiểm tra xe trả.");
        }

        if (!AllStaffChecksCompleted(booking))
        {
            return OperationResult.Failure(
                "Chưa đủ xác minh giao/trả để mở lại phần phụ phí.");
        }

        if (booking.Payments.Any(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status is PaymentStatus.AwaitingConfirmation or PaymentStatus.Paid))
        {
            return OperationResult.Failure(
                "Phụ phí đã báo chuyển hoặc đã thanh toán nên không thể mở lại để sửa.");
        }

        var pendingPayments = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Pending)
            .ToList();

        if (!pendingPayments.Any(payment =>
                AdditionalChargeSettlementPolicy.IsReadyMarker(payment.TransactionCode)))
        {
            return OperationResult.Failure("Phụ phí chưa được chốt nên không cần mở lại.");
        }

        _dbContext.Payments.RemoveRange(pendingPayments);

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar đang đối chiếu lại phụ phí",
            Message =
                $"Đơn #{booking.BookingId}: khoản phụ phí đang được nhân viên kiểm tra lại. Vui lòng chưa thanh toán cho đến khi có thông báo chốt mới."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CompleteAsync(
        int bookingId,
        bool requiresMaintenance,
        string? maintenanceNote,
        CancellationToken cancellationToken = default)
    {
        var normalizedMaintenanceNote = Normalize(maintenanceNote);
        if (requiresMaintenance && normalizedMaintenanceNote is null)
        {
            return OperationResult.Failure(
                "Đã đánh dấu xe cần bảo trì/sửa chữa thì phải ghi rõ nội dung cần xử lý.");
        }

        if (normalizedMaintenanceNote is { Length: > 1000 })
        {
            return OperationResult.Failure("Nội dung bảo trì tối đa 1.000 ký tự.");
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

        var extensionPayments = booking.Payments
            .Where(payment => payment.Type == PaymentType.Extension)
            .ToList();

        if (extensionPayments.Any(payment =>
                BookingWorkflowRules.BlocksVehicleReturnForExtensionPayment(payment.Status)))
        {
            return OperationResult.Failure(
                "Còn tiền gia hạn chuyển khoản/QR đang chờ đối soát. Cần xử lý giao dịch trước khi quyết toán.");
        }

        var paidExtensionAmount = extensionPayments
            .Where(payment => payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var effectivePaidExtensionAmount = booking.Extensions
            .Where(extension => extension.Status == BookingExtensionStatus.Paid)
            .Sum(extension => extension.AdditionalAmount);

        if (!BookingWorkflowRules.IsExtensionPaymentLedgerConsistent(
                paidExtensionAmount,
                effectivePaidExtensionAmount))
        {
            return OperationResult.Failure(
                "Dữ liệu gia hạn đang lệch giữa tiền đã thu và yêu cầu gia hạn có hiệu lực. " +
                "Không quyết toán tự động để tránh mất quyền lợi của khách.");
        }

        foreach (var extension in booking.Extensions.Where(extension =>
                     BookingWorkflowRules.ShouldCancelExtensionWhenVehicleReturns(extension.Status)))
        {
            var previousStatus = extension.Status;
            extension.Status = BookingExtensionStatus.Cancelled;
            extension.AdminNote = previousStatus == BookingExtensionStatus.Approved
                ? "Gia hạn đã được duyệt nhưng chưa thanh toán nên chưa có hiệu lực; tự động đóng khi quyết toán xe đã trả."
                : "Yêu cầu tự động đóng vì chuyến thuê đã kết thúc.";
            extension.DecidedAt = DateTime.UtcNow;
        }

        foreach (var payment in extensionPayments.Where(payment =>
                     payment.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Failed;
            payment.Method = PaymentMethods.NotSelected;
            payment.PaidAt = null;
            payment.TransactionCode = null;
        }

        var grossRentalPaid = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
            .Sum(payment => payment.Amount);
        var rentalRefundPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.VehicleSwapRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);
        var effectiveRentalPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossRentalPaid,
            rentalRefundPlanned);

        var grossDepositPaid = booking.Payments
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

        var effectiveDepositPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossDepositPaid,
            depositAlreadyRefundedOrPlanned);
        var requiredRentalAmount = Math.Max(
            0m,
            booking.TotalAmount - booking.AdditionalAmount);
        var upfrontSatisfied = BookingWorkflowRules.HasRequiredUpfrontPayment(
            requiredRentalAmount,
            effectiveRentalPaid,
            booking.DepositAmount,
            effectiveDepositPaid);

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

        var hasOpenSwapAdjustment = booking.Payments.Any(payment =>
            payment.Type == PaymentType.VehicleSwapAdjustment &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation);

        if (!upfrontSatisfied ||
            !additionalPaid ||
            hasOpenAdditionalPayment ||
            hasOpenSwapAdjustment)
        {
            return OperationResult.Failure(
                "Đơn vẫn còn khoản tiền chưa thanh toán, đang chờ đối soát hoặc chưa ghi nhận đủ tiền thuê/cọc.");
        }

        var depositAvailableBeforeNewDeduction = Math.Max(
            0m,
            effectiveDepositPaid - depositAlreadyDeducted);
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

        }

        booking.RefundAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
            .Sum(payment => payment.Amount);

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

        if (requiresMaintenance)
        {
            booking.Vehicle.Status = VehicleStatus.Maintenance;
        }
        else
        {
            booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
                _dbContext,
                booking.Vehicle,
                cancellationToken: cancellationToken);
        }

        if (requiresMaintenance)
        {
            _dbContext.MaintenanceRecords.Add(new MaintenanceRecord
            {
                VehicleId = booking.VehicleId,
                StartDate = DateTime.UtcNow,
                Content = normalizedMaintenanceNote!,
                Cost = 0,
                Mileage = booking.Vehicle.CurrentMileage,
                Status = MaintenanceStatus.InProgress
            });
        }

        var hasPendingRefund = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Refund &&
            BookingWorkflowRules.IsOpenRefundStatus(payment.Status));
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

        var finalizedPayment = pendingPayments.FirstOrDefault(payment =>
            AdditionalChargeSettlementPolicy.IsReadyMarker(payment.TransactionCode));

        foreach (var pendingPayment in pendingPayments)
        {
            if (pendingPayment != finalizedPayment)
            {
                _dbContext.Payments.Remove(pendingPayment);
            }
        }

        if (finalizedPayment is not null)
        {
            if (booking.AdditionalAmount > 0m)
            {
                finalizedPayment.Amount = booking.AdditionalAmount;
            }
            else
            {
                _dbContext.Payments.Remove(finalizedPayment);
            }
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
            (payment.Status is PaymentStatus.AwaitingConfirmation or PaymentStatus.Paid ||
             payment.Status == PaymentStatus.Pending &&
             AdditionalChargeSettlementPolicy.IsReadyMarker(payment.TransactionCode)));

    private static bool HasMissingAccessories(VehicleReturn vehicleReturn)
    {
        if (!string.IsNullOrWhiteSpace(vehicleReturn.AccessoryStatus) &&
            vehicleReturn.AccessoryStatus.StartsWith(
                AccessoriesMissingPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(vehicleReturn.Notes) &&
               vehicleReturn.Notes.TrimStart().StartsWith(
                   $"{ReturnAccessoriesLabel} {AccessoriesMissingPrefix}",
                   StringComparison.OrdinalIgnoreCase);
    }

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

