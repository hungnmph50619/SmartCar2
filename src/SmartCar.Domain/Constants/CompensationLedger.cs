namespace SmartCar.Domain.Constants;

/// <summary>
/// Tương thích với dữ liệu thử nghiệm cũ từng ghi khoản bồi thường tự động
/// khi gia hạn bất khả kháng làm ảnh hưởng đơn kế tiếp.
/// Chính sách hiện tại không tự động giữ hoặc khấu trừ cọc trong trường hợp này,
/// nên các marker cũ không còn làm giảm tiền cọc khả dụng.
/// </summary>
public static class CompensationLedger
{
    public const string Marker = "[NEXT_BOOKING_COMPENSATION]";

    public static decimal SumReservedAmount(string? value) => 0m;
}
