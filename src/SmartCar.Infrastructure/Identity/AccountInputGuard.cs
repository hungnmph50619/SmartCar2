using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;

namespace SmartCar.Infrastructure.Identity;

public static class AccountInputGuard
{
    public const string DuplicatePhoneMessage = "Số điện thoại này đã được sử dụng bởi tài khoản khác.";

    public static Task<bool> PhoneExistsAsync(
        UserManager<ApplicationUser> users, string phone, string? excludedUserId,
        CancellationToken cancellationToken = default)
    {
        phone = ProfileInputRules.NormalizePhoneNumber(phone);
        if (!ProfileInputRules.IsValidPhoneNumber(phone)) return Task.FromResult(false);
        var internationalPhone = $"+84{phone[1..]}";
        return users.Users.AnyAsync(user => user.Id != excludedUserId &&
            (user.PhoneNumber == phone || user.PhoneNumber == internationalPhone), cancellationToken);
    }

    // A pre-check alone cannot prevent two requests from using the same unique value.
    public static async Task<IdentityResult> SaveAsync(Func<Task<IdentityResult>> save)
    {
        try
        {
            return await save();
        }
        catch (DbUpdateException exception) when
            (exception.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627))
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateAccountData",
                Description = "Email, số điện thoại, CCCD hoặc mã nhân viên đã được sử dụng. Vui lòng kiểm tra lại."
            });
        }
    }
}
