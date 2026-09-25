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

        policy = PrepareForNewBooking(policy);
        if (row?.PolicyJson == null)
        {
            policy.Version = "legacy-" + policy.DepositHoldDays;
            policy.DamageCompensationTerms = policy.DamageCompensationTerms.Replace(
                "khoản bồi thường bằng giá hợp đồng của đơn thuê bị ảnh hưởng",
                "khoản bồi thường theo thiệt hại thực tế có căn cứ của đơn thuê bị ảnh hưởng");
        }
        return policy;
    }

    public static RentalPolicySnapshot PrepareForNewBooking(RentalPolicySnapshot policy)
    {
        // Chỉ tiền thuê cần còn đủ lead time mới hưởng cửa sổ hoàn 100%.
        // PolicyJson của booking đã tồn tại không bị sửa.
        policy.FreeCancellationRequiresMinimumLead = true;
        return policy;
    }
}
