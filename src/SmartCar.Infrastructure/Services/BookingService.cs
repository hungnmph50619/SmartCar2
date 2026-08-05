using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
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
            return BookingMutationResult.Failure("Thời gian nhận xe phải trước thời gian trả xe.");
        }

        var documentsValid = await _documentService.HasValidRentalDocumentsAsync(
            customerId,
            request.PickupDate,
            cancellationToken);

        if (!documentsValid)
        {
            return BookingMutationResult.Failure(
                "Bạn cần xác minh CCCD và GPLX còn hiệu lực trước khi đặt xe.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var vehicle = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null || vehicle.Status != VehicleStatus.Available)
        {
            return BookingMutationResult.Failure("Xe không tồn tại hoặc hiện không thể cho thuê.");
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

        var booking = new Booking
        {
            CustomerId = customerId,
            VehicleId = vehicle.VehicleId,
            PickupDate = request.PickupDate,
            ReturnDate = request.ReturnDate,
            DailyPrice = vehicle.DailyPrice,
            NumberOfDays = numberOfDays,
            RentalAmount = rentalAmount,
            AdditionalAmount = 0,
            TotalAmount = rentalAmount,
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
        CancellationToken cancellationToken = default)
    {
        return await ListQuery()
            .Where(booking => booking.CustomerId == customerId)
            .OrderByDescending(booking => booking.CreatedAt)
            .ToListAsync(cancellationToken);
    }

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

        if (!booking.Payments.Any(payment => payment.Type == PaymentType.Rental))
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Rental,
                Amount = booking.RentalAmount,
                Status = PaymentStatus.Pending,
                Method = "Mo phong"
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được xác nhận",
            Message = $"Đơn #{booking.BookingId} đã được xác nhận. Vui lòng thanh toán để giữ xe."
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
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);

        if (booking.Status != BookingStatus.Paid || !rentalPaid)
        {
            return OperationResult.Failure("Đơn phải thanh toán tiền thuê trước khi chuẩn bị giao xe.");
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
                TotalAmount = booking.TotalAmount,
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

        var booking = await query
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

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
            .ToList() ?? new List<ChargeSummaryDto>();

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
            DailyPrice = booking.DailyPrice,
            NumberOfDays = booking.NumberOfDays,
            RentalAmount = booking.RentalAmount,
            AdditionalAmount = booking.AdditionalAmount,
            TotalAmount = booking.TotalAmount,
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
                payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid),
            AdditionalChargePaid = booking.AdditionalAmount == 0 || booking.Payments.Any(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Status == PaymentStatus.Paid &&
                payment.Amount >= booking.AdditionalAmount),
            ExtensionPaid = booking.Extensions.All(extension =>
                extension.Status != BookingExtensionStatus.Approved),
            Payments = booking.Payments
                .OrderBy(payment => payment.PaymentId)
                .Select(payment => new PaymentSummaryDto(
                    payment.PaymentId,
                    payment.Type,
                    payment.Amount,
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
        _dbContext.Bookings.AnyAsync(booking =>
            booking.VehicleId == vehicleId &&
            (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
            BlockingStatuses.Contains(booking.Status) &&
            pickupDate < booking.ReturnDate &&
            returnDate > booking.PickupDate,
            cancellationToken);
}
