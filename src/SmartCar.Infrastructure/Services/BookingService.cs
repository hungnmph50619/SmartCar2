using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BookingService : IBookingService
{
    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IDocumentService _documentService;

    public BookingService(
        ApplicationDbContext dbContext,
        IDocumentService documentService)
    {
        _dbContext = dbContext;
        _documentService = documentService;
    }

    public async Task<BookingMutationResult> CreateAsync(
        string customerId,
        CreateBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return BookingMutationResult.Failure("Không xác định được khách hàng.");
        }

        if (!BookingDateRules.IsValidRange(request.PickupDate, request.ReturnDate))
        {
            return BookingMutationResult.Failure(
                "Thời gian nhận xe phải ở tương lai và trước thời gian trả xe.");
        }

        if (!Enum.IsDefined(request.PickupMethod))
        {
            return BookingMutationResult.Failure("Phương thức nhận xe không hợp lệ.");
        }

        if (request.PickupMethod == VehiclePickupMethod.Delivery)
        {
            if (string.IsNullOrWhiteSpace(request.DeliveryAddress))
            {
                return BookingMutationResult.Failure("Vui lòng nhập địa chỉ giao xe.");
            }

            if (request.DeliveryAddress.Trim().Length > 500)
            {
                return BookingMutationResult.Failure("Địa chỉ giao xe tối đa 500 ký tự.");
            }

            if (!request.DeliveryLatitude.HasValue || !request.DeliveryLongitude.HasValue)
            {
                return BookingMutationResult.Failure(
                    "Vui lòng tìm địa chỉ hoặc chọn chính xác điểm giao xe trên bản đồ để hệ thống tính phí giao xe.");
            }

            if (request.DeliveryLatitude.Value is < -90 or > 90 ||
                request.DeliveryLongitude.Value is < -180 or > 180)
            {
                return BookingMutationResult.Failure("Vị trí giao xe trên bản đồ không hợp lệ.");
            }

            var deliveryDistanceKm = RentalPolicy.CalculateDeliveryDistanceKm(
                request.DeliveryLatitude.Value,
                request.DeliveryLongitude.Value);

            if (deliveryDistanceKm > RentalPolicy.MaxDeliveryDistanceKm)
            {
                return BookingMutationResult.Failure(
                    $"SmartCar chỉ hỗ trợ giao xe trong bán kính tối đa {RentalPolicy.MaxDeliveryDistanceKm:0} km. " +
                    $"Điểm bạn chọn cách cửa hàng khoảng {deliveryDistanceKm:0.0} km.");
            }
        }

        var documentsValid = await _documentService.HasValidRentalDocumentsAsync(
            customerId,
            request.ReturnDate,
            cancellationToken);

        if (!documentsValid)
        {
            return BookingMutationResult.Failure(
                "Bạn cần xác minh CCCD và GPLX còn hiệu lực đến ngày trả xe.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var vehicle = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            return BookingMutationResult.Failure("Không tìm thấy xe.");
        }

        if (vehicle.Status is not (VehicleStatus.Available or VehicleStatus.Rented))
        {
            return BookingMutationResult.Failure(
                "Xe đang bảo trì, kiểm tra hoặc ngừng hoạt động nên chưa thể đặt.");
        }

        var hasOpenIncident = await _dbContext.VehicleIncidents.AnyAsync(
            item =>
                item.VehicleId == request.VehicleId &&
                item.IncidentType != IncidentType.TrafficFine &&
                item.Status != IncidentStatus.Resolved &&
                item.Status != IncidentStatus.Cancelled,
            cancellationToken);

        if (hasOpenIncident)
        {
            return BookingMutationResult.Failure("Xe đang có sự cố chưa xử lý nên chưa thể cho thuê.");
        }

        var hasValidRegistration = await HasValidVehicleDocumentAsync(
            request.VehicleId,
            VehicleDocumentType.Registration,
            request.PickupDate,
            request.ReturnDate,
            allowNoExpiry: true,
            cancellationToken);
        var hasValidInspection = await HasValidVehicleDocumentAsync(
            request.VehicleId,
            VehicleDocumentType.Inspection,
            request.PickupDate,
            request.ReturnDate,
            allowNoExpiry: false,
            cancellationToken);
        var hasValidInsurance = await HasValidVehicleDocumentAsync(
            request.VehicleId,
            VehicleDocumentType.Insurance,
            request.PickupDate,
            request.ReturnDate,
            allowNoExpiry: false,
            cancellationToken);
        var hasValidRoadFee = await HasValidVehicleDocumentAsync(
            request.VehicleId,
            VehicleDocumentType.RoadFee,
            request.PickupDate,
            request.ReturnDate,
            allowNoExpiry: false,
            cancellationToken);

        if (!hasValidRegistration || !hasValidInspection || !hasValidInsurance || !hasValidRoadFee)
        {
            return BookingMutationResult.Failure(
                "Xe chưa có đủ Đăng ký xe, Đăng kiểm, Bảo hiểm và Phí đường bộ còn hiệu lực đến ngày trả.");
        }

        var hasConflict = await HasConflictAsync(
            request.VehicleId,
            request.PickupDate,
            request.ReturnDate,
            null,
            cancellationToken);

        if (hasConflict)
        {
            return BookingMutationResult.Failure(
                "Xe vừa được khách khác đặt trong khoảng thời gian này.");
        }

        var numberOfDays = Math.Max(
            1,
            (int)Math.Ceiling((request.ReturnDate - request.PickupDate).TotalHours / 24d));
        var rentalAmount = numberOfDays * vehicle.DailyPrice;
        var depositAmount = RentalPolicy.CalculateDeposit(rentalAmount);
        var deliveryFee = RentalPolicy.CalculateDeliveryFee(
            request.PickupMethod,
            request.DeliveryLatitude,
            request.DeliveryLongitude);

        var booking = new Booking
        {
            CustomerId = customerId,
            VehicleId = vehicle.VehicleId,
            PickupDate = request.PickupDate,
            ReturnDate = request.ReturnDate,
            DailyPrice = vehicle.DailyPrice,
            NumberOfDays = numberOfDays,
            RentalAmount = rentalAmount,
            DepositAmount = depositAmount,
            AdditionalAmount = 0,
            TotalAmount = rentalAmount + deliveryFee,
            PickupMethod = request.PickupMethod,
            DeliveryAddress = request.PickupMethod == VehiclePickupMethod.Delivery
                ? request.DeliveryAddress?.Trim()
                : null,
            DeliveryLatitude = request.PickupMethod == VehiclePickupMethod.Delivery
                ? request.DeliveryLatitude
                : null,
            DeliveryLongitude = request.PickupMethod == VehiclePickupMethod.Delivery
                ? request.DeliveryLongitude
                : null,
            Status = BookingStatus.PendingConfirmation,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Bookings.Add(booking);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BookingMutationResult.Success(booking.BookingId);
    }

    public async Task<IReadOnlyList<BookingListItemDto>> GetCustomerBookingsAsync(
        string customerId,
        CancellationToken cancellationToken = default) =>
        await ListQuery()
            .Where(booking => booking.CustomerId == customerId)
            .OrderByDescending(booking => booking.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<BookingDetailsDto?> GetCustomerBookingAsync(
        int bookingId,
        string customerId,
        CancellationToken cancellationToken = default) =>
        GetDetailsAsync(bookingId, customerId, cancellationToken);

    public async Task<IReadOnlyList<BookingListItemDto>> GetAdminBookingsAsync(
        BookingStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = ListQuery();
        if (status.HasValue)
        {
            query = query.Where(booking => booking.Status == status.Value);
        }

        return await query
            .OrderByDescending(booking => booking.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<BookingDetailsDto?> GetAdminBookingAsync(
        int bookingId,
        CancellationToken cancellationToken = default) =>
        GetDetailsAsync(bookingId, null, cancellationToken);

    public async Task<OperationResult> ConfirmAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.PendingConfirmation)
        {
            return OperationResult.Failure("Chỉ đơn đang chờ xác nhận mới có thể được duyệt.");
        }

        if (booking.PickupDate <= DateTime.Now)
        {
            return OperationResult.Failure("Đã quá thời gian nhận xe, không thể xác nhận đơn.");
        }

        var documentsValid = await _documentService.HasValidRentalDocumentsAsync(
            booking.CustomerId,
            booking.ReturnDate,
            cancellationToken);

        if (!documentsValid)
        {
            return OperationResult.Failure("CCCD hoặc GPLX của khách không còn hiệu lực đến ngày trả xe.");
        }

        var hasConflict = await HasConflictAsync(
            booking.VehicleId,
            booking.PickupDate,
            booking.ReturnDate,
            booking.BookingId,
            cancellationToken);

        if (hasConflict)
        {
            return OperationResult.Failure("Xe đã phát sinh lịch thuê khác bị trùng thời gian.");
        }

        booking.Status = BookingStatus.PendingPayment;

        var deliveryFee = Math.Max(
            0m,
            booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);

        if (deliveryFee <= 0m &&
            booking.PickupMethod == VehiclePickupMethod.Delivery &&
            booking.DeliveryLatitude.HasValue &&
            booking.DeliveryLongitude.HasValue)
        {
            deliveryFee = RentalPolicy.CalculateDeliveryFee(
                booking.PickupMethod,
                booking.DeliveryLatitude,
                booking.DeliveryLongitude);
        }

        var rentalPaymentAmount = booking.RentalAmount + deliveryFee;
        booking.TotalAmount = booking.RentalAmount + deliveryFee + booking.AdditionalAmount;

        var rentalPayment = booking.Payments
            .FirstOrDefault(payment => payment.Type == PaymentType.Rental);

        if (rentalPayment is null)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Rental,
                Amount = rentalPaymentAmount,
                Status = PaymentStatus.Pending,
                Method = PaymentMethods.NotSelected
            });
        }
        else if (rentalPayment.Status == PaymentStatus.Pending)
        {
            rentalPayment.Amount = rentalPaymentAmount;
        }

        if (booking.DepositAmount > 0)
        {
            var depositPayment = booking.Payments
                .FirstOrDefault(payment => payment.Type == PaymentType.Deposit);

            if (depositPayment is null)
            {
                booking.Payments.Add(new Payment
                {
                    Type = PaymentType.Deposit,
                    Amount = booking.DepositAmount,
                    Status = PaymentStatus.Pending,
                    Method = PaymentMethods.NotSelected
                });
            }
            else if (depositPayment.Status == PaymentStatus.Pending)
            {
                depositPayment.Amount = booking.DepositAmount;
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được xác nhận",
            Message =
                $"Đơn #{booking.BookingId} đã được xác nhận. " +
                $"Tổng thanh toán trước khi nhận xe: {(rentalPaymentAmount + booking.DepositAmount):N0} đồng."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RejectAsync(
        int bookingId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.PendingConfirmation)
        {
            return OperationResult.Failure("Chỉ đơn đang chờ xác nhận mới có thể bị từ chối.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do từ chối.");
        }

        booking.Status = BookingStatus.Rejected;
        booking.CancelReason = reason.Trim();

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê bị từ chối",
            Message = $"Đơn #{booking.BookingId} bị từ chối. Lý do: {booking.CancelReason}"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> MarkReadyForPickupAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental &&
            payment.Status == PaymentStatus.Paid);

        var paidDeposit = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var depositRefundPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);
        var depositSatisfied = booking.DepositAmount <= 0 ||
            Math.Max(0m, paidDeposit - depositRefundPlanned) >= booking.DepositAmount;

        var hasOpenSwapPayment = booking.Payments.Any(payment =>
            payment.Type == PaymentType.VehicleSwapAdjustment &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation);

        if (booking.Status != BookingStatus.Paid ||
            !rentalPaid ||
            !depositSatisfied ||
            hasOpenSwapPayment)
        {
            return OperationResult.Failure(
                hasOpenSwapPayment
                    ? "Cần thanh toán xong chênh lệch đổi xe trước khi chuẩn bị giao xe."
                    : "Đơn phải thanh toán đủ tiền thuê và tiền cọc trước khi chuẩn bị giao xe.");
        }

        booking.Status = BookingStatus.ReadyForPickup;
        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Xe đã sẵn sàng bàn giao",
            Message = $"Xe của đơn #{booking.BookingId} đã sẵn sàng để nhận."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private IQueryable<BookingListItemDto> ListQuery() =>
        _dbContext.Bookings
            .AsNoTracking()
            .Select(booking => new BookingListItemDto
            {
                BookingId = booking.BookingId,
                CustomerId = booking.CustomerId,
                CustomerName = _dbContext.Users
                    .Where(user => user.Id == booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? string.Empty,
                CustomerPhone = _dbContext.Users
                    .Where(user => user.Id == booking.CustomerId)
                    .Select(user => user.PhoneNumber)
                    .FirstOrDefault(),
                VehicleId = booking.VehicleId,
                VehicleName = booking.Vehicle.VehicleName,
                LicensePlate = booking.Vehicle.LicensePlate,
                PrimaryImagePath = booking.Vehicle.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.ImagePath)
                    .FirstOrDefault(),
                PickupDate = booking.PickupDate,
                ReturnDate = booking.ReturnDate,
                PickupMethod = booking.PickupMethod,
                DeliveryAddress = booking.DeliveryAddress,
                TotalAmount = booking.TotalAmount,
                DepositAmount = booking.DepositAmount,
                Status = booking.Status,
                CreatedAt = booking.CreatedAt
            });

    private async Task<BookingDetailsDto?> GetDetailsAsync(
        int bookingId,
        string? customerId,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Vehicle)
                .ThenInclude(vehicle => vehicle.Images)
            .Include(booking => booking.Payments)
            .Include(booking => booking.Extensions)
            .Include(booking => booking.Review)
            .Include(booking => booking.Handover)
            .Include(booking => booking.VehicleReturn)
                .ThenInclude(vehicleReturn => vehicleReturn!.AdditionalCharges)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            query = query.Where(booking => booking.CustomerId == customerId);
        }

        var booking = await query.FirstOrDefaultAsync(
            item => item.BookingId == bookingId,
            cancellationToken);

        if (booking is null)
        {
            return null;
        }

        var customer = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == booking.CustomerId)
            .Select(user => new { user.FullName, user.PhoneNumber })
            .FirstOrDefaultAsync(cancellationToken);

        var charges = booking.VehicleReturn?.AdditionalCharges
            .Select(charge => new ChargeSummaryDto(
                charge.AdditionalChargeId,
                charge.ChargeType,
                charge.Description,
                charge.Amount))
            .ToList()
            ?? new List<ChargeSummaryDto>();

        var depositDeduction = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method == PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var actualAdditionalAmount = booking.VehicleReturn is null
            ? booking.AdditionalAmount
            : charges.Sum(charge => charge.Amount);
        var legacyMisclassifiedDeduction = Math.Min(
            depositDeduction,
            Math.Max(0m, booking.AdditionalAmount - actualAdditionalAmount));
        var normalizedAdditionalAmount = Math.Max(
            0m,
            booking.AdditionalAmount - legacyMisclassifiedDeduction);
        var normalizedTotalAmount = Math.Max(
            0m,
            booking.TotalAmount - legacyMisclassifiedDeduction);

        var paidDeposit = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var depositRefundPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);
        var effectiveDeposit = Math.Max(0m, paidDeposit - depositRefundPlanned);

        var cashAdditionalPaid = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method != PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        return new BookingDetailsDto
        {
            BookingId = booking.BookingId,
            CustomerId = booking.CustomerId,
            CustomerName = customer?.FullName ?? string.Empty,
            CustomerPhone = customer?.PhoneNumber,
            VehicleId = booking.VehicleId,
            VehicleName = booking.Vehicle.VehicleName,
            LicensePlate = booking.Vehicle.LicensePlate,
            PrimaryImagePath = booking.Vehicle.Images
                .OrderByDescending(image => image.IsPrimary)
                .ThenBy(image => image.SortOrder)
                .Select(image => image.ImagePath)
                .FirstOrDefault(),
            PickupDate = booking.PickupDate,
            ReturnDate = booking.ReturnDate,
            PickupMethod = booking.PickupMethod,
            DeliveryAddress = booking.DeliveryAddress,
            DeliveryLatitude = booking.DeliveryLatitude,
            DeliveryLongitude = booking.DeliveryLongitude,
            DailyPrice = booking.DailyPrice,
            NumberOfDays = booking.NumberOfDays,
            RentalAmount = booking.RentalAmount,
            DepositAmount = booking.DepositAmount,
            AdditionalAmount = normalizedAdditionalAmount,
            DepositDeductionAmount = depositDeduction,
            TotalAmount = normalizedTotalAmount,
            Status = booking.Status,
            CreatedAt = booking.CreatedAt,
            CancelReason = booking.CancelReason,
            CancelledBy = booking.CancelledBy,
            CancelledAt = booking.CancelledAt,
            RefundAmount = booking.RefundAmount,
            RefundReason = booking.RefundReason,
            NoShowMarkedAt = booking.NoShowMarkedAt,
            HasHandover = booking.Handover is not null,
            HasReturn = booking.VehicleReturn is not null,
            HasReview = booking.Review is not null,
            RentalPaid = booking.Payments.Any(payment =>
                payment.Type == PaymentType.Rental &&
                payment.Status == PaymentStatus.Paid),
            DepositPaid = booking.DepositAmount <= 0 || effectiveDeposit >= booking.DepositAmount,
            AdditionalChargePaid = normalizedAdditionalAmount == 0 ||
                cashAdditionalPaid >= normalizedAdditionalAmount,
            ExtensionPaid = booking.Extensions.All(extension =>
                extension.Status != BookingExtensionStatus.Approved),
            Payments = booking.Payments
                .Where(payment => payment.Method != PaymentMethods.DepositDeduction)
                .OrderBy(payment => payment.PaymentId)
                .Select(payment => new PaymentSummaryDto(
                    payment.PaymentId,
                    payment.Type,
                    payment.Amount,
                    payment.Method,
                    payment.Status,
                    payment.PaidAt,
                    payment.TransactionCode))
                .ToList(),
            AdditionalCharges = charges,
            Extensions = booking.Extensions
                .OrderByDescending(extension => extension.RequestedAt)
                .Select(extension => new ExtensionSummaryDto(
                    extension.BookingExtensionId,
                    extension.OriginalReturnDate,
                    extension.RequestedReturnDate,
                    extension.AdditionalDays,
                    extension.AdditionalAmount,
                    extension.Status,
                    extension.CustomerNote,
                    extension.AdminNote))
                .ToList()
        };
    }

    private Task<bool> HasConflictAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        int? excludedBookingId,
        CancellationToken cancellationToken) =>
        _dbContext.Bookings.AnyAsync(
            booking =>
                booking.VehicleId == vehicleId &&
                (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
                BlockingStatuses.Contains(booking.Status) &&
                pickupDate < booking.ReturnDate &&
                returnDate > booking.PickupDate,
            cancellationToken);

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
             (document.ExpiryDate.HasValue && document.ExpiryDate.Value.Date >= requiredUntil.Date)),
            cancellationToken);
}