using SmartCar.Application.Features.Bookings;

namespace SmartCar.Web.Services;

/// <summary>
/// Dọn các đơn giữ chỗ đã quá hạn ngay cả khi không có người mở trang Booking.
/// Chính sách hết hạn thật sự nằm trong PolicyAwareBookingService; hosted service chỉ kích hoạt
/// việc kiểm tra định kỳ để xe được giải phóng đúng thời gian.
/// </summary>
public sealed class BookingReservationCleanupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingReservationCleanupService> _logger;

    public BookingReservationCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<BookingReservationCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();

                // PolicyAwareBookingService gọi ExpireStaleReservationsAsync trước khi trả danh sách.
                // Không cần giữ kết quả; mục tiêu ở đây là kích hoạt cleanup theo cùng một policy.
                _ = await bookingService.GetAdminBookingsAsync(
                    status: null,
                    cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Không thể chạy vòng dọn đơn giữ chỗ đã hết hạn.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
