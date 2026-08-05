using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.VehicleDocuments;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class VehicleDocumentService : IVehicleDocumentService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public VehicleDocumentService(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public Task<IReadOnlyList<VehicleDocumentDto>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        Query().OrderBy(item => item.DaysUntilExpiry ?? int.MaxValue)
            .ThenBy(item => item.VehicleName)
            .ToListAsync(cancellationToken)
            .ContinueWith<IReadOnlyList<VehicleDocumentDto>>(
                task => task.Result,
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    public Task<IReadOnlyList<VehicleDocumentDto>> GetByVehicleAsync(
        int vehicleId,
        CancellationToken cancellationToken = default) =>
        Query().Where(item => item.VehicleId == vehicleId)
            .OrderBy(item => item.DocumentType)
            .ThenByDescending(item => item.IssuedDate)
            .ToListAsync(cancellationToken)
            .ContinueWith<IReadOnlyList<VehicleDocumentDto>>(
                task => task.Result,
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    public async Task<OperationResult> CreateAsync(
        SaveVehicleDocumentRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentNumber))
        {
            return OperationResult.Failure("Số giấy tờ không được để trống.");
        }

        if (request.ExpiryDate.HasValue && request.ExpiryDate.Value.Date < request.IssuedDate.Date)
        {
            return OperationResult.Failure("Ngày hết hạn không được trước ngày cấp.");
        }

        var vehicle = await _dbContext.Vehicles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        var document = new VehicleDocument
        {
            VehicleId = request.VehicleId,
            DocumentType = request.DocumentType,
            DocumentNumber = request.DocumentNumber.Trim(),
            IssuedDate = request.IssuedDate,
            ExpiryDate = request.ExpiryDate,
            ImagePath = Normalize(request.ImagePath),
            Notes = Normalize(request.Notes),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.VehicleDocuments.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Create",
            nameof(VehicleDocument),
            document.VehicleDocumentId.ToString(),
            $"Thêm {document.DocumentType} cho xe {vehicle.LicensePlate}.",
            newValues: JsonSerializer.Serialize(new
            {
                document.VehicleId,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate
            }),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAsync(
        int documentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.VehicleDocuments
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.VehicleDocumentId == documentId, cancellationToken);

        if (document is null)
        {
            return OperationResult.Failure("Không tìm thấy giấy tờ xe.");
        }

        var oldValues = JsonSerializer.Serialize(new
        {
            document.VehicleId,
            document.DocumentType,
            document.DocumentNumber,
            document.IssuedDate,
            document.ExpiryDate
        });

        _dbContext.VehicleDocuments.Remove(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Delete",
            nameof(VehicleDocument),
            documentId.ToString(),
            $"Xóa {document.DocumentType} của xe {document.Vehicle.LicensePlate}.",
            oldValues: oldValues,
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private IQueryable<VehicleDocumentDto> Query()
    {
        var today = DateTime.Today;

        return _dbContext.VehicleDocuments
            .AsNoTracking()
            .Select(document => new VehicleDocumentDto(
                document.VehicleDocumentId,
                document.VehicleId,
                document.Vehicle.VehicleName,
                document.Vehicle.LicensePlate,
                document.DocumentType,
                document.DocumentNumber,
                document.IssuedDate,
                document.ExpiryDate,
                document.ImagePath,
                document.Notes,
                document.ExpiryDate.HasValue && document.ExpiryDate.Value < today,
                document.ExpiryDate.HasValue
                    ? EF.Functions.DateDiffDay(today, document.ExpiryDate.Value)
                    : null));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
