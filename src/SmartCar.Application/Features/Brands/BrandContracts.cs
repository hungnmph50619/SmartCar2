using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Brands;

public sealed record BrandDto(
    int BrandId,
    string BrandName,
    bool IsActive,
    int VehicleCount);

public interface IBrandService
{
    Task<IReadOnlyList<BrandDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BrandDto>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(string brandName, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateAsync(int brandId, string brandName, CancellationToken cancellationToken = default);
    Task<OperationResult> SetActiveAsync(int brandId, bool isActive, CancellationToken cancellationToken = default);
}
