using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;

namespace SmartCar.Infrastructure.Identity;

public static class AccountInputGuard
{
    public const string DuplicatePhoneMessage = "Số điện thoại này đã được sử dụng bởi tài khoản khác.";
    public const string DuplicateCitizenIdMessage = "Số CCCD này đã được sử dụng bởi tài khoản khác.";
    public const string DuplicateEmployeeCodeMessage = "Mã nhân viên này đã được sử dụng bởi tài khoản khác.";

    public static IQueryable<ApplicationUser> CitizenIdCollisions(
        IQueryable<ApplicationUser> users,
        string? citizenIdNumber,
        string? excludedUserId)
    {
        var citizenId = (citizenIdNumber ?? string.Empty).Trim();
        if (citizenId.Length != 12 || citizenId.Any(character => !char.IsDigit(character)))
        {
            return users.Where(_ => false);
        }

        return users.Where(user =>
            user.Id != excludedUserId &&
            user.CitizenIdNumber == citizenId);
    }

    public static IQueryable<ApplicationUser> EmployeeCodeCollisions(
        IQueryable<ApplicationUser> users,
        string? employeeCode,
        string? excludedUserId)
    {
        var code = (employeeCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return users.Where(_ => false);
        }

        return users.Where(user =>
            user.Id != excludedUserId &&
            user.EmployeeCode == code);
    }

    public static IQueryable<ApplicationUser> PhoneCollisions(
        IQueryable<ApplicationUser> users,
        string? phoneNumber,
        string? excludedUserId)
    {
        var phone = ProfileInputRules.NormalizePhoneNumber(phoneNumber);
        if (!ProfileInputRules.IsValidPhoneNumber(phone))
        {
            return users.Where(_ => false);
        }

        var internationalPhone = $"+84{phone[1..]}";
        return users.Where(user =>
            user.Id != excludedUserId &&
            (user.PhoneNumber == phone || user.PhoneNumber == internationalPhone));
    }

    public static Task<bool> PhoneExistsAsync(
        UserManager<ApplicationUser> users, string phone, string? excludedUserId,
        CancellationToken cancellationToken = default) =>
        PhoneCollisions(users.Users, phone, excludedUserId).AnyAsync(cancellationToken);

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
