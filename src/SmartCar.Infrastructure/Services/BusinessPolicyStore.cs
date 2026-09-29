using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

public static class BusinessPolicyStore
{
    public sealed class SettingRow
    {
        public int DepositHoldDays { get; set; }
        public string? PolicyJson { get; set; }
    }

    public static async Task<RentalPolicySnapshot> ReadAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database.SqlQueryRaw<SettingRow>(
            "SELECT [DepositHoldDays], [PolicyJson] FROM [dbo].[BusinessSettings] WHERE [BusinessSettingId] = 1")
            .ToListAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        var policy = RentalPolicySnapshot.FromJson(row?.PolicyJson);
        policy.DepositHoldDays = row == null ? DepositHoldPolicy.DefaultDays : DepositHoldPolicy.NormalizeDays(row.DepositHoldDays);

        // BusinessSettings cũ của Quy_2 có thể chưa có DeliveryLeadMinutes do field từng bị bỏ.
        // Cấu hình hiện hành/đơn mới dùng lại mặc định 30 phút; booking cũ vẫn đọc snapshot riêng.
        if (!HasProperty(row?.PolicyJson, nameof(RentalPolicySnapshot.DeliveryLeadMinutes)))
        {
            policy.DeliveryLeadMinutes = RentalPolicy.DeliveryLeadMinutes;
        }

        // Hủy trong cửa sổ miễn phí sau thanh toán được hoàn 100% tiền thuê.
        // Không có thêm điều kiện tối thiểu bao nhiêu giờ trước thời điểm nhận xe.
        if (row?.PolicyJson == null)
        {
            policy.Version = "legacy-" + policy.DepositHoldDays;
            policy.DamageCompensationTerms = policy.DamageCompensationTerms.Replace(
                "khoản bồi thường bằng giá hợp đồng của đơn thuê bị ảnh hưởng",
                "khoản bồi thường theo thiệt hại thực tế có căn cứ của đơn thuê bị ảnh hưởng");
        }
        return policy;
    }

    private static bool HasProperty(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out _);
    }
}
