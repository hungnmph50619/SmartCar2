using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;
using Xunit;

namespace SmartCar.Tests;

public sealed class CounterRentalWorkflowTests
{
    [Fact]
    public async Task CounterRental_RejectsActiveCustomerWithoutDefaultRefundAccount()
    {
        await using var db = CreateDbContext();
        await SeedActiveCustomerAsync(db);
        var bookings = new BookingServiceStub();
        var controller = CreateController(
            db,
            bookings,
            new BankAccountServiceStub(defaultAccount: null));

        var result = await controller.CounterRental(ValidModel(), default);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, bookings.CreateCalls);
        Assert.Contains(
            controller.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage.Contains("tài khoản ngân hàng", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CounterRental_WithDefaultRefundAccount_CanProceedToBookingCreation()
    {
        await using var db = CreateDbContext();
        await SeedActiveCustomerAsync(db);
        var bookings = new BookingServiceStub();
        var bank = new UserBankAccountDto(
            1,
            "customer-1",
            "VCB",
            "Vietcombank",
            "0123456789",
            "NGUYEN VAN A",
            true,
            true,
            DateTime.UtcNow,
            DateTime.UtcNow);
        var controller = CreateController(
            db,
            bookings,
            new BankAccountServiceStub(bank));

        var result = await controller.CounterRental(ValidModel(), default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StaffController.Details), redirect.ActionName);
        Assert.Equal(1, bookings.CreateCalls);
    }

    [Fact]
    public async Task CounterRental_ImmediatePickup_UsesCurrentTimeInsteadOfScheduledTime()
    {
        await using var db = CreateDbContext();
        await SeedActiveCustomerAsync(db);
        var bookings = new BookingServiceStub();
        var bank = new UserBankAccountDto(1, "customer-1", "VCB", "Vietcombank",
            "0123456789", "NGUYEN VAN A", true, true, DateTime.UtcNow, DateTime.UtcNow);
        var controller = CreateController(db, bookings, new BankAccountServiceStub(bank));
        var before = DateTime.Now;
        var model = ValidModel();
        model.IsImmediatePickup = true;

        await controller.CounterRental(model, default);

        Assert.NotNull(bookings.LastRequest);
        Assert.True(bookings.LastRequest.IsImmediateCounterRental);
        Assert.InRange(bookings.LastRequest.PickupDate, before, DateTime.Now);
        Assert.True(bookings.LastRequest.IsStaffCounterRental);
    }

    [Fact]
    public async Task CounterRental_FromCustomerList_PrefillsActiveCustomer()
    {
        await using var db = CreateDbContext();
        await SeedActiveCustomerAsync(db);
        var controller = CreateController(db, new BookingServiceStub(), new BankAccountServiceStub(null));

        var result = Assert.IsType<ViewResult>(await controller.CounterRental("customer-1", default));
        var model = Assert.IsType<StaffCounterRentalViewModel>(result.Model);
        Assert.Equal("customer-1", model.CustomerId);
        Assert.Equal("Nguyễn Văn A", model.SelectedCustomerName);
    }

    [Fact]
    public async Task CounterRental_FromCustomerList_RejectsUnknownCustomer()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, new BookingServiceStub(), new BankAccountServiceStub(null));

        var result = Assert.IsType<ViewResult>(await controller.CounterRental("unknown", default));
        Assert.Equal(string.Empty, Assert.IsType<StaffCounterRentalViewModel>(result.Model).CustomerId);
    }

    private static StaffCounterRentalViewModel ValidModel() => new()
    {
        CustomerId = "customer-1",
        VehicleId = 12,
        PickupDate = DateTime.Now.AddHours(2),
        ReturnDate = DateTime.Now.AddDays(1).AddHours(2)
    };

    private static StaffController CreateController(
        ApplicationDbContext db,
        IBookingService bookings,
        IUserBankAccountService bankAccounts)
    {
        var controller = new StaffController(db, bookings, new AuditServiceStub(), bankAccounts);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TempDataProviderStub());
        return controller;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"counter-rental-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task SeedActiveCustomerAsync(ApplicationDbContext db)
    {
        const string roleId = "customer-role";
        db.Roles.Add(new IdentityRole
        {
            Id = roleId,
            Name = RoleNames.Customer,
            NormalizedName = RoleNames.Customer.ToUpperInvariant()
        });
        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1",
            UserName = "customer@example.com",
            NormalizedUserName = "CUSTOMER@EXAMPLE.COM",
            Email = "customer@example.com",
            NormalizedEmail = "CUSTOMER@EXAMPLE.COM",
            FullName = "Nguyễn Văn A",
            IsActive = true
        });
        db.UserRoles.Add(new IdentityUserRole<string>
        {
            UserId = "customer-1",
            RoleId = roleId
        });
        await db.SaveChangesAsync();
    }

    private sealed class BookingServiceStub : IBookingService
    {
        public int CreateCalls { get; private set; }
        public CreateBookingRequest? LastRequest { get; private set; }

        public Task<BookingMutationResult> CreateAsync(
            string customerId,
            CreateBookingRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastRequest = request;
            return Task.FromResult(BookingMutationResult.Success(777));
        }

        public Task<IReadOnlyList<BookingListItemDto>> GetCustomerBookingsAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookingListItemDto>>(Array.Empty<BookingListItemDto>());

        public Task<BookingDetailsDto?> GetCustomerBookingAsync(int bookingId, string customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookingDetailsDto?>(null);

        public Task<IReadOnlyList<BookingListItemDto>> GetAdminBookingsAsync(BookingStatus? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookingListItemDto>>(Array.Empty<BookingListItemDto>());

        public Task<BookingDetailsDto?> GetAdminBookingAsync(int bookingId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookingDetailsDto?>(null);

        public Task<OperationResult> ConfirmAsync(int bookingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> RejectAsync(int bookingId, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> MarkReadyForPickupAsync(int bookingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
    }

    private sealed class BankAccountServiceStub : IUserBankAccountService
    {
        private readonly UserBankAccountDto? _defaultAccount;

        public BankAccountServiceStub(UserBankAccountDto? defaultAccount)
        {
            _defaultAccount = defaultAccount;
        }

        public IReadOnlyList<BankOption> Banks { get; } = Array.Empty<BankOption>();

        public Task<UserBankAccountDto?> GetDefaultAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_defaultAccount);

        public Task SaveDefaultAsync(
            string userId,
            string bankCode,
            string accountNumber,
            string accountHolderName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AuditServiceStub : IAuditService
    {
        public Task WriteAsync(
            string? userId,
            string action,
            string entityName,
            string entityId,
            string description,
            string? oldValues = null,
            string? newValues = null,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(int take = 200, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuditLogSearchResult> SearchAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuditLogDto?> GetByIdAsync(long auditLogId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDataProviderStub : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
