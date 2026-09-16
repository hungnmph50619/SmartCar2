using Microsoft.AspNetCore.Identity;
using SmartCar.Domain.Constants;

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
                Code = "DuplicatePhoneNumber",
                Description = AccountInputGuard.DuplicatePhoneMessage
            });
        }

        if (AccountInputGuard.CitizenIdCollisions(manager.Users, user.CitizenIdNumber, user.Id).Any())
        {
            errors.Add(new IdentityError
            {
                Code = "DuplicateCitizenIdNumber",
                Description = AccountInputGuard.DuplicateCitizenIdMessage
            });
        }

        if (AccountInputGuard.EmployeeCodeCollisions(manager.Users, user.EmployeeCode, user.Id).Any())
        {
            errors.Add(new IdentityError
            {
                Code = "DuplicateEmployeeCode",
                Description = AccountInputGuard.DuplicateEmployeeCodeMessage
            });
        }

        return Task.FromResult(errors.Count == 0
            ? IdentityResult.Success
            : IdentityResult.Failed(errors.ToArray()));
    }
}
