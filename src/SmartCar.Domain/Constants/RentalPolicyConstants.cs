namespace SmartCar.Domain.Constants;

public static class RentalPolicyConstants
{
    public const decimal SecurityDepositAmount = 5_000_000m;
    public const string SecurityDepositCashMethod = "Tiền mặt";
    public const string SecurityDepositTransferMethod = "Chuyển khoản";
    public const string SecurityDepositSettlementMethod = "Đối trừ tiền cọc";
    public const string SecurityDepositMixedSettlementMethod = "Đối trừ cọc + thanh toán bổ sung";
    public const string SecurityDepositRefundMethod = "Hoàn cọc bảo đảm";

    // DEMO ONLY: cho phép bỏ qua ràng buộc mốc thời gian giao/trả để chạy nhanh toàn bộ luồng.
    // Sau khi demo đổi về false để khôi phục kiểm tra nghiệp vụ bình thường.
    public const bool DemoBypassRentalTimelineValidation = true;
}
