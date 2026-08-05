using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Promotions;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class PromotionService : IPromotionService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public PromotionService(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<PromotionDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;

        return await _dbContext.Promotions
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new PromotionDto(
                item.PromotionId,
                item.Code,
                item.Name,
                item.PromotionType,
                item.Value,
                item.MaximumDiscount,
                item.MinimumRentalAmount,
                item.StartAt,
                item.EndAt,
                item.UsageLimit,
                item.UsedCount,
                item.IsActive,
                item.IsActive &&
                item.StartAt <= now &&
                item.EndAt >= now &&
                (!item.UsageLimit.HasValue || item.UsedCount < item.UsageLimit.Value)))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> CreateAsync(
        SavePromotionRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return OperationResult.Failure(validationError);
        }

        var code = NormalizeCode(request.Code);
        var exists = await _dbContext.Promotions.AnyAsync(
            item => item.Code == code,
            cancellationToken);

        if (exists)
        {
            return OperationResult.Failure("Mã khuyến mãi đã tồn tại.");
        }

        var promotion = new Promotion
        {
            Code = code,
            Name = request.Name.Trim(),
            PromotionType = request.PromotionType,
            Value = request.Value,
            MaximumDiscount = request.MaximumDiscount,
            MinimumRentalAmount = request.MinimumRentalAmount,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
            UsageLimit = request.UsageLimit,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Promotions.Add(promotion);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Create",
            nameof(Promotion),
            promotion.PromotionId.ToString(),
            $"Tạo mã khuyến mãi {promotion.Code}.",
            newValues: JsonSerializer.Serialize(new
            {
                promotion.Code,
                promotion.Name,
                promotion.PromotionType,
                promotion.Value,
                promotion.MaximumDiscount,
                promotion.MinimumRentalAmount,
                promotion.StartAt,
                promotion.EndAt,
                promotion.UsageLimit,
                promotion.IsActive
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ChangeStatusAsync(
        int promotionId,
        bool isActive,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var promotion = await _dbContext.Promotions
            .FirstOrDefaultAsync(item => item.PromotionId == promotionId, cancellationToken);

        if (promotion is null)
        {
            return OperationResult.Failure("Không tìm thấy mã khuyến mãi.");
        }

        var oldStatus = promotion.IsActive;
        promotion.IsActive = isActive;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ChangeStatus",
            nameof(Promotion),
            promotionId.ToString(),
            $"{(isActive ? "Kích hoạt" : "Ngừng")} mã {promotion.Code}.",
            oldValues: oldStatus.ToString(),
            newValues: isActive.ToString(),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<PromotionApplyResult> ApplyToBookingAsync(
        int bookingId,
        string customerId,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return PromotionApplyResult.Failure("Vui lòng nhập mã khuyến mãi.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return PromotionApplyResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (booking.Status is not (BookingStatus.PendingConfirmation or BookingStatus.PendingPayment))
        {
            return PromotionApplyResult.Failure("Chỉ áp dụng khuyến mãi trước khi thanh toán tiền thuê.");
        }

        if (booking.Payments.Any(item =>
                item.Type == PaymentType.Rental && item.Status == PaymentStatus.Paid))
        {
            return PromotionApplyResult.Failure("Đơn đã thanh toán nên không thể áp dụng khuyến mãi.");
        }

        var normalizedCode = NormalizeCode(code);
        var now = DateTime.Now;
        var promotion = await _dbContext.Promotions
            .FirstOrDefaultAsync(item =>
                item.Code == normalizedCode &&
                item.IsActive &&
                item.StartAt <= now &&
                item.EndAt >= now,
                cancellationToken);

        if (promotion is null)
        {
            return PromotionApplyResult.Failure("Mã khuyến mãi không tồn tại hoặc đã hết hiệu lực.");
        }

        if (promotion.UsageLimit.HasValue && promotion.UsedCount >= promotion.UsageLimit.Value)
        {
            return PromotionApplyResult.Failure("Mã khuyến mãi đã hết lượt sử dụng.");
        }

        if (booking.RentalAmount < promotion.MinimumRentalAmount)
        {
            return PromotionApplyResult.Failure(
                $"Đơn thuê phải từ {promotion.MinimumRentalAmount:N0} đồng để dùng mã này.");
        }

        if (booking.PromotionCode == promotion.Code)
        {
            return PromotionApplyResult.Failure("Mã khuyến mãi đã được áp dụng cho đơn.");
        }

        var discount = CalculateDiscount(promotion, booking.RentalAmount);
        booking.PromotionCode = promotion.Code;
        booking.DiscountAmount = discount;
        booking.TotalAmount = Math.Max(0, booking.RentalAmount - discount + booking.AdditionalAmount);

        var pendingRentalPayment = booking.Payments.FirstOrDefault(item =>
            item.Type == PaymentType.Rental && item.Status == PaymentStatus.Pending);
        if (pendingRentalPayment is not null)
        {
            pendingRentalPayment.Amount = booking.RentalAmount - discount;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "ApplyPromotion",
            nameof(Booking),
            bookingId.ToString(),
            $"Áp dụng mã {promotion.Code} cho đơn #{bookingId}.",
            newValues: JsonSerializer.Serialize(new
            {
                booking.PromotionCode,
                booking.DiscountAmount,
                booking.TotalAmount
            }),
            cancellationToken: cancellationToken);

        return PromotionApplyResult.Success(discount, booking.TotalAmount);
    }

    public async Task<OperationResult> RemoveFromBookingAsync(
        int bookingId,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (booking.Status is not (BookingStatus.PendingConfirmation or BookingStatus.PendingPayment))
        {
            return OperationResult.Failure("Không thể gỡ khuyến mãi sau khi thanh toán.");
        }

        if (string.IsNullOrWhiteSpace(booking.PromotionCode))
        {
            return OperationResult.Failure("Đơn chưa áp dụng mã khuyến mãi.");
        }

        var oldCode = booking.PromotionCode;
        booking.PromotionCode = null;
        booking.DiscountAmount = 0;
        booking.TotalAmount = booking.RentalAmount + booking.AdditionalAmount;

        var pendingRentalPayment = booking.Payments.FirstOrDefault(item =>
            item.Type == PaymentType.Rental && item.Status == PaymentStatus.Pending);
        if (pendingRentalPayment is not null)
        {
            pendingRentalPayment.Amount = booking.RentalAmount;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "RemovePromotion",
            nameof(Booking),
            bookingId.ToString(),
            $"Gỡ mã {oldCode} khỏi đơn #{bookingId}.",
            oldValues: oldCode,
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private static string? ValidateRequest(SavePromotionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return "Mã và tên chương trình không được để trống.";
        }

        if (request.StartAt >= request.EndAt)
        {
            return "Thời gian bắt đầu phải trước thời gian kết thúc.";
        }

        if (request.Value <= 0 || request.MinimumRentalAmount < 0 || request.MaximumDiscount < 0)
        {
            return "Giá trị khuyến mãi không hợp lệ.";
        }

        if (request.PromotionType == PromotionType.Percentage && request.Value > 100)
        {
            return "Mức giảm phần trăm không được vượt quá 100%.";
        }

        if (request.UsageLimit.HasValue && request.UsageLimit.Value <= 0)
        {
            return "Giới hạn lượt sử dụng phải lớn hơn 0.";
        }

        return null;
    }

    private static decimal CalculateDiscount(Promotion promotion, decimal rentalAmount)
    {
        var discount = promotion.PromotionType == PromotionType.Percentage
            ? rentalAmount * promotion.Value / 100m
            : promotion.Value;

        if (promotion.MaximumDiscount.HasValue)
        {
            discount = Math.Min(discount, promotion.MaximumDiscount.Value);
        }

        return Math.Round(Math.Min(discount, rentalAmount), 0, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeCode(string value) =>
        value.Trim().ToUpperInvariant().Replace(" ", string.Empty);
}
