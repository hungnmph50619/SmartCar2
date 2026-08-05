using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Maintenance;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class MaintenanceService : IMaintenanceService
{
    private readonly ApplicationDbContext _dbContext;

    public MaintenanceService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<MaintenanceDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _dbContext.MaintenanceRecords
            .AsNoTracking()
            .Include(record => record.Vehicle)
            .OrderBy(record => record.Status)
            .ThenByDescending(record => record.StartDate)
            .ToListAsync(cancellationToken);

        return records
            .Select(Map)
            .ToList();
    }

    public async Task<MaintenanceDto?> GetByIdAsync(
        int maintenanceId,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.MaintenanceRecords
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(
                item => item.MaintenanceRecordId == maintenanceId,
                cancellationToken);

        return record is null ? null : Map(record);
    }

    public async Task<OperationResult> CreateAsync(
        CreateMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content) || request.Cost < 0 || request.Mileage < 0)
        {
            return OperationResult.Failure("Thông tin bảo trì không hợp lệ.");
        }

        var vehicle = await _dbContext.Vehicles
            .Include(item => item.MaintenanceRecords)
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        if (vehicle.Status is VehicleStatus.Rented or VehicleStatus.Inspection)
        {
            return OperationResult.Failure("Không thể đưa xe đang thuê hoặc chờ kiểm tra vào bảo trì.");
        }

        if (vehicle.MaintenanceRecords.Any(record => record.Status == MaintenanceStatus.InProgress))
        {
            return OperationResult.Failure("Xe đang có một phiếu bảo trì chưa hoàn tất.");
        }

        vehicle.Status = VehicleStatus.Maintenance;
        vehicle.MaintenanceRecords.Add(new MaintenanceRecord
        {
            StartDate = request.StartDate,
            Content = request.Content.Trim(),
            Cost = request.Cost,
            ServiceProvider = Normalize(request.ServiceProvider),
            Mileage = request.Mileage,
            Status = MaintenanceStatus.InProgress
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CompleteAsync(
        int maintenanceId,
        decimal finalCost,
        string? completionNote,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.MaintenanceRecords
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.MaintenanceRecordId == maintenanceId, cancellationToken);

        if (record is null || record.Status != MaintenanceStatus.InProgress)
        {
            return OperationResult.Failure("Phiếu bảo trì không tồn tại hoặc đã được xử lý.");
        }

        if (finalCost < 0)
        {
            return OperationResult.Failure("Chi phí bảo trì không hợp lệ.");
        }

        record.Cost = finalCost;
        record.CompletedDate = DateTime.UtcNow;
        record.Status = MaintenanceStatus.Completed;
        if (!string.IsNullOrWhiteSpace(completionNote))
        {
            record.Content = $"{record.Content}\nKết quả: {completionNote.Trim()}";
        }

        record.Vehicle.CurrentMileage = Math.Max(record.Vehicle.CurrentMileage, record.Mileage);
        record.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            record.Vehicle,
            excludedMaintenanceId: record.MaintenanceRecordId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CancelAsync(
        int maintenanceId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do hủy bảo trì.");
        }

        var record = await _dbContext.MaintenanceRecords
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.MaintenanceRecordId == maintenanceId, cancellationToken);

        if (record is null || record.Status != MaintenanceStatus.InProgress)
        {
            return OperationResult.Failure("Phiếu bảo trì không tồn tại hoặc đã được xử lý.");
        }

        record.Status = MaintenanceStatus.Cancelled;
        record.CompletedDate = DateTime.UtcNow;
        record.Content = $"{record.Content}\nĐã hủy: {reason.Trim()}";
        record.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            record.Vehicle,
            excludedMaintenanceId: record.MaintenanceRecordId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static MaintenanceDto Map(MaintenanceRecord record) =>
        new(
            record.MaintenanceRecordId,
            record.VehicleId,
            record.Vehicle.VehicleName,
            record.Vehicle.LicensePlate,
            record.StartDate,
            record.CompletedDate,
            record.Content,
            record.Cost,
            record.ServiceProvider,
            record.Mileage,
            record.Status);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
