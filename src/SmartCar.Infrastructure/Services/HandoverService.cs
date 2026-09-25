using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Handovers;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class HandoverService : IHandoverService
{
    private readonly ApplicationDbContext _dbContext;

    public HandoverService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityVerifiedByStaffId))
        {
            return OperationResult.Failure("Không xác định được nhân viên đã trực tiếp kiểm tra người nhận xe.");
        }

        if (request.IdentityFaceSessionId == Guid.Empty)
        {
            return OperationResult.Failure("Cần chụp ảnh khuôn mặt người nhận xe trực tiếp trước khi lập biên bản.");
        }

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

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

        var activeCustomer = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(user =>
                user.Id == booking.CustomerId &&
                user.IsActive,
                cancellationToken);
        if (!activeCustomer)
        {
            return OperationResult.Failure(
                "Tài khoản khách đang bị khóa hoặc không còn hoạt động. Dừng bàn giao và báo quản lý.");
        }

        if (booking.Handover is not null)
        {
            return OperationResult.Failure("Đơn đã có biên bản giao xe.");
        }

        var verifiedDocuments = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == booking.CustomerId &&
                document.Status == DocumentStatus.Verified &&
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.CitizenIdBack ||
                 document.DocumentType == DocumentTypes.DrivingLicense ||
                 document.DocumentType == DocumentTypes.DrivingLicenseBack))
            .ToListAsync(cancellationToken);

        var citizenFront = verifiedDocuments.FirstOrDefault(document =>
            document.DocumentType == DocumentTypes.CitizenId);
        var citizenBack = verifiedDocuments.FirstOrDefault(document =>
            document.DocumentType == DocumentTypes.CitizenIdBack);
        var licenseFront = verifiedDocuments.FirstOrDefault(document =>
            document.DocumentType == DocumentTypes.DrivingLicense);
        var licenseBack = verifiedDocuments.FirstOrDefault(document =>
            document.DocumentType == DocumentTypes.DrivingLicenseBack);

        if (citizenFront is null || citizenBack is null ||
            licenseFront is null || licenseBack is null ||
            !string.Equals(citizenFront.DocumentNumber, citizenBack.DocumentNumber, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(licenseFront.DocumentNumber, licenseBack.DocumentNumber, StringComparison.OrdinalIgnoreCase) ||
            !citizenFront.ExpiryDate.HasValue || citizenFront.ExpiryDate.Value.Date < booking.ReturnDate.Date ||
            !licenseFront.ExpiryDate.HasValue || licenseFront.ExpiryDate.Value.Date < booking.ReturnDate.Date)
        {
            return OperationResult.Failure(
                "CCCD/GPLX đã xác minh không còn đủ cả hai mặt hoặc không còn hiệu lực đến ngày trả. Dừng bàn giao và kiểm tra lại KYC.");
        }

        var faceSession = await _dbContext.Set<IdentityCaptureSession>()
            .FirstOrDefaultAsync(item =>
                item.IdentityCaptureSessionId == request.IdentityFaceSessionId,
                cancellationToken);

        if (faceSession is null ||
            faceSession.Purpose != IdentityCapturePurposes.Handover ||
            faceSession.BookingId != booking.BookingId ||
            faceSession.TargetCustomerId != booking.CustomerId ||
            faceSession.CreatedByUserId != request.IdentityVerifiedByStaffId ||
            !faceSession.CanConsume(DateTime.UtcNow) ||
            !IdentityCaptureMethods.All.Contains(faceSession.CaptureMethod ?? string.Empty, StringComparer.Ordinal))
        {
            return OperationResult.Failure(
                "Ảnh mặt người nhận không hợp lệ, không thuộc đúng đơn/khách hoặc đã được dùng. Vui lòng chụp lại tại quầy.");
        }

        var counterEvidence = await _dbContext.Set<IdentityCaptureSession>()
            .Where(item =>
                item.BookingId == booking.BookingId &&
                item.TargetCustomerId == booking.CustomerId &&
                item.CreatedByUserId == request.IdentityVerifiedByStaffId &&
                !item.ConsumedAt.HasValue &&
                item.CompletedAt.HasValue &&
                item.ExpiresAt > DateTime.UtcNow &&
                item.ImagePath != null &&
                item.CaptureMethod == IdentityCaptureMethods.StaffCounterDocument &&
                (item.Purpose == IdentityCapturePurposes.HandoverCitizenFront ||
                 item.Purpose == IdentityCapturePurposes.HandoverCitizenBack))
            .OrderByDescending(item => item.CompletedAt)
            .ToListAsync(cancellationToken);

        var citizenFrontSession = counterEvidence
            .FirstOrDefault(item => item.Purpose == IdentityCapturePurposes.HandoverCitizenFront);
        var citizenBackSession = counterEvidence
            .FirstOrDefault(item => item.Purpose == IdentityCapturePurposes.HandoverCitizenBack);

        if (citizenFrontSession is null || citizenBackSession is null)
        {
            return OperationResult.Failure(
                "Cần chụp và lưu đủ CCCD mặt trước + mặt sau của khách đang có mặt tại quầy trước khi lập biên bản giao xe.");
        }

        var grossRentalPaidAmount = booking.Payments
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
            grossRentalPaidAmount,
            rentalRefundPlanned);

        var grossDepositPaidAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var depositRefundPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var effectiveDepositPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossDepositPaidAmount,
            depositRefundPlanned);
        var requiredRentalAmount = Math.Max(
            0m,
            booking.TotalAmount - booking.AdditionalAmount);
        var upfrontSatisfied = BookingWorkflowRules.HasRequiredUpfrontPayment(
            requiredRentalAmount,
            effectiveRentalPaid,
            booking.DepositAmount,
            effectiveDepositPaid);

        var hasOpenSwapPayment = booking.Payments.Any(payment =>
            payment.Type == PaymentType.VehicleSwapAdjustment &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation);

        if (hasOpenSwapPayment)
        {
            return OperationResult.Failure(
                "Khách cần thanh toán xong chênh lệch đổi xe trước khi giao xe.");
        }

        if (!upfrontSatisfied)
        {
            return OperationResult.Failure(
                "Khách phải thanh toán đủ tiền thuê và tiền cọc trước khi giao xe.");
        }

        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            return OperationResult.Failure("Xe hiện không ở trạng thái sẵn sàng.");
        }

        if (!await HasRequiredVehicleDocumentsAsync(
                booking.VehicleId,
                booking.PickupDate,
                booking.ReturnDate,
                cancellationToken))
        {
            return OperationResult.Failure(
                "Giấy tờ xe không còn đủ hiệu lực cho toàn bộ chuyến thuê. Dừng bàn giao và kiểm tra lại Đăng ký xe, Đăng kiểm, Bảo hiểm và Phí đường bộ.");
        }

        var preparedAt = DateTime.Now;
        if (!BookingWorkflowRules.CanPrepareHandover(
                preparedAt, booking.PickupDate, booking.ReturnDate))
        {
            return OperationResult.Failure(
                $"Chỉ lập biên bản giao từ {booking.PickupDate:dd/MM/yyyy HH:mm} đến trước {booking.ReturnDate:dd/MM/yyyy HH:mm}.");
        }

        if (request.Mileage < booking.Vehicle.CurrentMileage)
        {
            return OperationResult.Failure(
                $"Số km giao xe không được nhỏ hơn số km hiện tại ({booking.Vehicle.CurrentMileage:N0} km).");
        }

        if (!TryParseFuelPercent(request.FuelLevel, out var fuelPercent))
        {
            return OperationResult.Failure("Mức nhiên liệu khi giao phải là số từ 0 đến 100%.");
        }

        if (!request.PenaltyPolicyAccepted)
        {
            return OperationResult.Failure(
                "Cần xác nhận đã thông báo và khách đã đồng ý chính sách phí/phạt trước khi giao xe.");
        }

        if (string.IsNullOrWhiteSpace(request.ImagePaths))
        {
            return OperationResult.Failure("Biên bản giao xe phải có ảnh đối chiếu tình trạng xe.");
        }

        var rentalDays = Math.Max(
            1,
            (int)Math.Ceiling((booking.ReturnDate - booking.PickupDate).TotalDays));
        var verifiedAt = DateTime.UtcNow;

        booking.Handover = new VehicleHandover
        {
            HandoverAt = preparedAt,
            Mileage = request.Mileage,
            FuelLevel = $"{fuelPercent}%",
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            Accessories = Normalize(request.Accessories),
            ImagePaths = Normalize(request.ImagePaths),
            ReceiverFaceImagePath = faceSession.ImagePath,
            Notes = Normalize(request.Notes),
            IncludedKilometers = rentalDays * booking.Policy.IncludedKilometersPerDay,
            ExcessKmFeePerKm = booking.Policy.ExcessKilometerFee,
            LateReturnFeeMultiplier = booking.Policy.LateReturnFeeMultiplier,
            TrafficFineTerms = booking.Policy.TrafficFineTerms,
            DamageCompensationTerms = booking.Policy.DamageCompensationTerms,
            PenaltyPolicyAccepted = true,
            CustomerIdentityVerified = true,
            IdentityVerifiedByStaffId = request.IdentityVerifiedByStaffId,
            IdentityVerifiedAt = verifiedAt
        };

        faceSession.ConsumedAt = verifiedAt;
        citizenFrontSession.ConsumedAt = verifiedAt;
        citizenBackSession.ConsumedAt = verifiedAt;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private async Task<bool> HasRequiredVehicleDocumentsAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        CancellationToken cancellationToken)
    {
        return await HasValidVehicleDocumentAsync(
                   vehicleId,
                   VehicleDocumentType.Registration,
                   pickupDate,
                   returnDate,
                   allowNoExpiry: true,
                   cancellationToken) &&
               await HasValidVehicleDocumentAsync(
                   vehicleId,
                   VehicleDocumentType.Inspection,
                   pickupDate,
                   returnDate,
                   allowNoExpiry: false,
                   cancellationToken) &&
               await HasValidVehicleDocumentAsync(
                   vehicleId,
                   VehicleDocumentType.Insurance,
                   pickupDate,
                   returnDate,
                   allowNoExpiry: false,
                   cancellationToken) &&
               await HasValidVehicleDocumentAsync(
                   vehicleId,
                   VehicleDocumentType.RoadFee,
                   pickupDate,
                   returnDate,
                   allowNoExpiry: false,
                   cancellationToken);
    }

    private Task<bool> HasValidVehicleDocumentAsync(
        int vehicleId,
        VehicleDocumentType documentType,
        DateTime requiredFrom,
        DateTime requiredUntil,
        bool allowNoExpiry,
        CancellationToken cancellationToken) =>
        _dbContext.VehicleDocuments.AnyAsync(document =>
            document.VehicleId == vehicleId &&
            document.DocumentType == documentType &&
            document.IssuedDate.Date <= requiredFrom.Date &&
            ((allowNoExpiry && !document.ExpiryDate.HasValue) ||
             (document.ExpiryDate.HasValue &&
              document.ExpiryDate.Value.Date >= requiredUntil.Date)),
            cancellationToken);

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

        return int.TryParse(normalized, out percent) && percent is >= 0 and <= 100;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

