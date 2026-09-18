using Microsoft.AspNetCore.Identity;

namespace SmartCar.Infrastructure.Identity;

public sealed class ApplicationUserUniquenessValidator : IUserValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user)
    {
        var errors = new List<IdentityError>();

        if (AccountInputGuard.PhoneCollisions(manager.Users, user.PhoneNumber, user.Id).Any())
        {
            errors.Add(new IdentityError
            {
                Code = "DuplicateAccountData",
                Description = AccountInputGuard.DuplicatePhoneMessage
            });
        }

        if (AccountInputGuard.CitizenIdCollisions(manager.Users, user.CitizenIdNumber, user.Id).Any())
        {
            errors.Add(new IdentityError
            {
                Code = "DuplicateAccountData",
                Description = AccountInputGuard.DuplicateCitizenIdMessage
            });
        }

        if (AccountInputGuard.EmployeeCodeCollisions(manager.Users, user.EmployeeCode, user.Id).Any())
        {
            errors.Add(new IdentityError
            {
                Code = "DuplicateAccountData",
                Description = AccountInputGuard.DuplicateEmployeeCodeMessage
            });
        }

        return Task.FromResult(errors.Count == 0
            ? IdentityResult.Success
            : IdentityResult.Failed(errors.ToArray()));
    }
}
