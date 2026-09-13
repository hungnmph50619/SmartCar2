using SmartCar.Application.Common;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Enums;

namespace SmartCar.Infrastructure.Services;

internal sealed class PolicyAwareVehicleService : IVehicleService
{
    private readonly VehicleService _inner;
    private readonly BookingReservationPolicy _policy;

    public PolicyAwareVehicleService(
        VehicleService inner,
        BookingReservationPolicy policy)
    {
        _inner = inner;
        _policy = policy;
    }

    public Task<IReadOnlyList<VehicleDto>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetAllAsync(cancellationToken);

    public Task<VehicleDto?> GetByIdAsync(
        int vehicleId,
        CancellationToken cancellationToken = default) =>
        _inner.GetByIdAsync(vehicleId, cancellationToken);

    public async Task<IReadOnlyList<VehicleDto>> SearchAvailableAsync(
        VehicleSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);

        var candidates = await _inner.SearchAvailableAsync(request, cancellationToken);
        var result = new List<VehicleDto>(candidates.Count);

        foreach (var vehicle in candidates)
        {
            // Màn tìm xe hiện chỉ lọc theo thời gian, chưa chọn phương thức nhận xe.
            // Vì vậy dùng StorePickup cho bước tìm kiếm chung; nếu khách chọn giao tận nơi
            // khi tạo đơn, BookingService sẽ kiểm tra lại với DeliveryLeadMinutes.
            var bufferedConflict = await _policy.HasBufferedConflictAsync(
                vehicle.VehicleId,
                request.PickupDate,
                request.ReturnDate,
                VehiclePickupMethod.StorePickup,
                null,
                cancellationToken);

            if (bufferedConflict)
            {
                continue;
            }

            var blockedUntil = await _policy.GetActualTurnaroundBlockedUntilAsync(
                vehicle.VehicleId,
                request.PickupDate,
                null,
                cancellationToken);

            if (blockedUntil.HasValue && blockedUntil.Value > request.PickupDate)
            {
                continue;
            }

            result.Add(vehicle);
        }

        return result;
    }

    public Task<VehicleMutationResult> CreateAsync(
        CreateVehicleRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.CreateAsync(request, cancellationToken);

    public Task<OperationResult> UpdateAsync(
        UpdateVehicleRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.UpdateAsync(request, cancellationToken);

    public Task<OperationResult> ChangeStatusAsync(
        int vehicleId,
        VehicleStatus status,
        CancellationToken cancellationToken = default) =>
        _inner.ChangeStatusAsync(vehicleId, status, cancellationToken);

    public Task<OperationResult> AddImageAsync(
        int vehicleId,
        string imagePath,
        bool setAsPrimary,
        CancellationToken cancellationToken = default) =>
        _inner.AddImageAsync(vehicleId, imagePath, setAsPrimary, cancellationToken);

    public Task<OperationResult> SetPrimaryImageAsync(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken = default) =>
        _inner.SetPrimaryImageAsync(vehicleId, imageId, cancellationToken);

    public Task<OperationResult> DeleteImageAsync(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteImageAsync(vehicleId, imageId, cancellationToken);
}