using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Bookings;

/// <summary>
/// Kiểm tra một đơn ở trạng thái Chờ xác nhận trước khi nhân viên gửi quản trị viên duyệt.
/// Chỉ kiểm tra, không thay đổi trạng thái đơn.
/// </summary>
public interface IBookingReviewService
{
    Task<OperationResult> ValidateForStaffReviewAsync(
        int bookingId,
        CancellationToken cancellationToken = default);
}
