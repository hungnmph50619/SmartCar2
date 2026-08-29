namespace SmartCar.Domain.Constants;

/// <summary>
/// Marker chỉ dùng để nhận diện dữ liệu thử nghiệm cũ từng tự động gán
/// mức bồi thường cho gia hạn bất khả kháng. Dữ liệu mới không dùng marker này:
/// quản trị viên phải nhập thiệt hại thực tế có căn cứ và hệ thống ghi nhận
/// khoản khấu trừ cọc bằng giao dịch riêng. Vì vậy marker cũ không còn giữ cọc.
/// </summary>
public static class CompensationLedger
{
    public const string Marker = "[NEXT_BOOKING_COMPENSATION]";

    public static decimal SumReservedAmount(string? value) => 0m;
}
