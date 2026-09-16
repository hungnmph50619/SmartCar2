using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Web.ViewModels;
using Xunit;

namespace SmartCar.Tests;

public sealed class StaffAccountRegressionTests
{
    [Fact]
    public async Task RepeatedLockAndUnlockPreserveRequestedState()
    {
        using var users = new TestUserManager();
        var audit = new TestAudit();
        var controller = new AdminStaffController(users, null!, audit);
        Prepare(controller);

        await controller.LockStaff(users.Staff.Id, default);
        await controller.LockStaff(users.Staff.Id, default);
        Assert.False(users.Staff.IsActive);
        Assert.Equal(1, audit.Writes);

        await controller.UnlockStaff(users.Staff.Id, default);
        await controller.UnlockStaff(users.Staff.Id, default);
        Assert.True(users.Staff.IsActive);
        Assert.Equal(2, audit.Writes);
    }

    [Fact]
    public async Task FirstLoginRejectsUnchangedTemporaryPassword()
    {
        using var users = new TestUserManager();
        var controller = new AccountController(null!, null!, users, null!, null!);
        Prepare(controller);
        var result = await controller.FirstLoginPassword(new ChangePasswordViewModel
        {
            CurrentPassword = "Temporary123!",
            NewPassword = "Temporary123!",
            ConfirmPassword = "Temporary123!"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(users.Staff.MustChangePassword);
        Assert.Equal(0, users.PasswordChanges);
    }

    [Fact]
    public async Task MissingCreateFieldsReturnValidationWithoutDatabaseAccess()
    {
        using var users = new TestUserManager();
        var controller = new AdminStaffController(users, null!, new TestAudit());
        Prepare(controller);
        controller.ModelState.AddModelError("Email", "Required");
        var result = await controller.Create(new AdminStaffCreateViewModel
        {
            FullName = null!, Email = null!, PhoneNumber = null!, CitizenIdNumber = null!
        }, default);
        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("+84901234567", "0901234567")]
    [InlineData("0901234567", "0901234567")]
    public void PhoneNormalizationHandlesMissingAndInternationalInput(string? input, string expected) =>
        Assert.Equal(expected, ProfileInputRules.NormalizePhoneNumber(input));

    [Theory]
    [InlineData("Nguyễn Văn An", true)]
    [InlineData("Nguyễn Văn 123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StaffAndSelfProfileUseSameNameRules(string? name, bool expected)
    {
        Assert.Equal(expected, ValidProperty(new AdminStaffCreateViewModel(), "FullName", name));
        Assert.Equal(expected, ValidProperty(new AdminStaffEditViewModel(), "FullName", name));
        Assert.Equal(expected, ValidProperty(new ProfileViewModel(), "FullName", name));
    }

    [Fact]
    public void ProfileRejectsValuesLongerThanDatabaseColumns()
    {
        Assert.True(ValidProperty(new ProfileViewModel(), "FullName", new string('A', 100)));
        Assert.False(ValidProperty(new ProfileViewModel(), "FullName", new string('A', 101)));
        Assert.True(ValidProperty(new ProfileViewModel(), "Address", new string('A', 250)));
        Assert.False(ValidProperty(new ProfileViewModel(), "Address", new string('A', 251)));
    }

    private static bool ValidProperty(object model, string member, object? value) =>
        Validator.TryValidateProperty(value, new ValidationContext(model) { MemberName = member }, new List<ValidationResult>());

    private static void Prepare(Controller controller)
    {
        var context = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new TestTempData());
    }

    private sealed class TestUserManager : UserManager<ApplicationUser>
    {
        public ApplicationUser Staff { get; } = new() { Id = "staff", IsActive = true, MustChangePassword = true };
        public int PasswordChanges { get; private set; }

        public TestUserManager() : base(
            new UserStore<ApplicationUser>(new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options)),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance) { }

        public override Task<ApplicationUser?> FindByIdAsync(string userId) => Task.FromResult<ApplicationUser?>(Staff);
        public override Task<ApplicationUser?> GetUserAsync(ClaimsPrincipal principal) => Task.FromResult<ApplicationUser?>(Staff);
        public override Task<bool> IsInRoleAsync(ApplicationUser user, string role) => Task.FromResult(role == RoleNames.Staff);
        public override Task<IdentityResult> UpdateAsync(ApplicationUser user) => Task.FromResult(IdentityResult.Success);
        public override Task<IdentityResult> UpdateSecurityStampAsync(ApplicationUser user) => Task.FromResult(IdentityResult.Success);
        public override Task<IdentityResult> ChangePasswordAsync(ApplicationUser user, string currentPassword, string newPassword)
        {
            PasswordChanges++;
            return Task.FromResult(IdentityResult.Success);
        }
    }

    private sealed class TestTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestAudit : IAuditService
    {
        public int Writes { get; private set; }
        public Task WriteAsync(string? userId, string action, string entityName, string entityId, string description,
            string? oldValues = null, string? newValues = null, string? ipAddress = null, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(int take = 200, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogSearchResult> SearchAsync(AuditLogQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogDto?> GetByIdAsync(long auditLogId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
