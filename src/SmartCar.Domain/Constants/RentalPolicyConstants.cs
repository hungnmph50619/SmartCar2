namespace SmartCar.Domain.Constants;

public static class RentalPolicyConstants
{
    public const decimal SecurityDepositAmount = 5_000_000m;
    public const string SecurityDepositCashMethod = "Tiền mặt";
    public const string SecurityDepositTransferMethod = "Chuyển khoản";
    public const string SecurityDepositSettlementMethod = "Đối trừ tiền cọc";
    public const string SecurityDepositMixedSettlementMethod = "Đối trừ cọc + thanh toán bổ sung";
    public const string SecurityDepositRefundMethod = "Hoàn cọc bảo đảm";
}
