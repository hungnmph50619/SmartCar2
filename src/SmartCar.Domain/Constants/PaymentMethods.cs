using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartCar.Domain.Constants;

public static class PaymentMethods
{
    public const string NotSelected = "Chưa chọn";
    public const string Simulation = "Mô phỏng";
    public const string BankQr = "QR ngân hàng";
    public const string BankTransferRefund = "Chuyển khoản hoàn tiền";
}