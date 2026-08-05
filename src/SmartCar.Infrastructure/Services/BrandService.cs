using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Brands;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BrandService : IBrandService
{
    private readonly ApplicationDbContext _dbContext;

    public BrandService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<BrandDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Brands
            .AsNoTracking()
            .OrderBy(brand => brand.BrandName)
            .Select(brand => new BrandDto(
                brand.BrandId,
                brand.BrandName,
                brand.IsActive,
                brand.Vehicles.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BrandDto>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Brands
            .AsNoTracking()
            .Where(brand => brand.IsActive)
            .OrderBy(brand => brand.BrandName)
            .Select(brand => new BrandDto(
                brand.BrandId,
                brand.BrandName,
                brand.IsActive,
                brand.Vehicles.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> CreateAsync(
        string brandName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = brandName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return OperationResult.Failure("Tên hãng xe không được để trống.");
        }

        var exists = await _dbContext.Brands
            .AnyAsync(brand => brand.BrandName == normalizedName, cancellationToken);

        if (exists)
        {
            return OperationResult.Failure("Hãng xe này đã tồn tại.");
        }

        _dbContext.Brands.Add(new Brand
        {
            BrandName = normalizedName,
            IsActive = true
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> UpdateAsync(
        int brandId,
        string brandName,
        CancellationToken cancellationToken = default)
    {
        var brand = await _dbContext.Brands
            .FirstOrDefaultAsync(item => item.BrandId == brandId, cancellationToken);

        if (brand is null)
        {
            return OperationResult.Failure("Không tìm thấy hãng xe.");
        }

        var normalizedName = brandName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return OperationResult.Failure("Tên hãng xe không được để trống.");
        }

        var duplicated = await _dbContext.Brands
            .AnyAsync(item =>
                item.BrandId != brandId && item.BrandName == normalizedName,
                cancellationToken);

        if (duplicated)
        {
            return OperationResult.Failure("Tên hãng xe đã được sử dụng.");
        }

        brand.BrandName = normalizedName;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SetActiveAsync(
        int brandId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var brand = await _dbContext.Brands
            .FirstOrDefaultAsync(item => item.BrandId == brandId, cancellationToken);

        if (brand is null)
        {
            return OperationResult.Failure("Không tìm thấy hãng xe.");
        }

        brand.IsActive = isActive;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }
}
