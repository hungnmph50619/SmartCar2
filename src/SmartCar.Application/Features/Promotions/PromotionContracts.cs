using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Promotions;

public sealed record PromotionDto(
    int PromotionId,
    string Code,
    string Name,
    PromotionType PromotionType,
    decimal Value,
    decimal? MaximumDiscount,
    decimal MinimumRentalAmount,
    DateTime StartAt,
    DateTime EndAt,
    int? UsageLimit,
    int UsedCount,
    bool IsActive,
    bool IsCurrentlyValid);

public sealed record SavePromotionRequest(
    string Code,
    string Name,
    PromotionType PromotionType,
    decimal Value,
    decimal? MaximumDiscount,
    decimal MinimumRentalAmount,
    DateTime StartAt,
    DateTime EndAt,
    int? UsageLimit,
    bool IsActive);

public sealed record PromotionApplyResult(
    bool Succeeded,
    decimal DiscountAmount,
    decimal NewTotalAmount,
    IReadOnlyCollection<string> Errors)
{
    public static PromotionApplyResult Success(decimal discountAmount, decimal newTotalAmount) =>
        new(true, discountAmount, newTotalAmount, Array.Empty<string>());

    public static PromotionApplyResult Failure(params string[] errors) =>
        new(false, 0, 0, errors);
}

public interface IPromotionService
{
    Task<IReadOnlyList<PromotionDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(SavePromotionRequest request, string adminId, CancellationToken cancellationToken = default);
    Task<OperationResult> ChangeStatusAsync(int promotionId, bool isActive, string adminId, CancellationToken cancellationToken = default);
    Task<PromotionApplyResult> ApplyToBookingAsync(int bookingId, string customerId, string code, CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveFromBookingAsync(int bookingId, string customerId, CancellationToken cancellationToken = default);
}
