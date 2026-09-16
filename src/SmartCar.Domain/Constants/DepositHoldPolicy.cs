namespace SmartCar.Domain.Constants;

public static class DepositHoldPolicy
{
    // Giá trị mặc định chỉ dùng khi cấu hình DB chưa tồn tại hoặc dữ liệu cũ cần fallback.
    // Giá trị nghiệp vụ thực tế được Admin cấu hình trong BusinessSettings.
    public const int DefaultDays = 15;

    // Admin chỉ được cấu hình tối đa 30 ngày cho các đơn tạo mới.
    public const int MaxConfigurableDays = 30;

    // Giữ ngưỡng cũ để đọc an toàn các booking lịch sử đã chụp policy trước đây.
    public const int MaxDays = 90;

    public static int NormalizeDays(int days) =>
        Math.Clamp(days, 0, MaxDays);

    public static DateTime CalculateEligibleAt(DateTime returnedAt, int days) =>
        returnedAt.AddDays(NormalizeDays(days));
}
