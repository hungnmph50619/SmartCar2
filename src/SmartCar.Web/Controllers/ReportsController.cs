using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Reports;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReportsController : Controller
{
    private static readonly PaymentType[] RevenueTypes =
    {
        PaymentType.Rental,
        PaymentType.Extension,
        PaymentType.AdditionalCharge,
        PaymentType.VehicleSwapAdjustment
    };

    private readonly IReportService _reportService;
    private readonly ApplicationDbContext _dbContext;

    public ReportsController(
        IReportService reportService,
        ApplicationDbContext dbContext)
    {
        _reportService = reportService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        DateTime? fromDate,
        DateTime? toDate,
        CancellationToken cancellationToken)
    {
        var to = (toDate ?? DateTime.Today).Date;
        var from = (fromDate ?? to.AddDays(-29)).Date;
        if (from > to)
        {
            (from, to) = (to, from);
        }

        var endExclusive = to.AddDays(1);

        var paymentBreakdown = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.PaidAt.HasValue &&
                payment.PaidAt.Value >= from &&
                payment.PaidAt.Value < endExclusive &&
                RevenueTypes.Contains(payment.Type))
            .GroupBy(payment => payment.Method)
            .Select(group => new
            {
                Method = group.Key,
                Amount = group.Sum(payment => payment.Amount)
            })
            .ToListAsync(cancellationToken);

        var cashRevenue = paymentBreakdown
            .Where(item => item.Method == PaymentMethods.Cash)
            .Sum(item => item.Amount);

        var bankQrRevenue = paymentBreakdown
            .Where(item => item.Method == PaymentMethods.BankQr)
            .Sum(item => item.Amount);

        var otherRevenue = paymentBreakdown
            .Where(item =>
                item.Method != PaymentMethods.Cash &&
                item.Method != PaymentMethods.BankQr)
            .Sum(item => item.Amount);

        ViewBag.CashRevenue = cashRevenue;
        ViewBag.BankQrRevenue = bankQrRevenue;
        ViewBag.OtherRevenue = otherRevenue;

        var report = await _reportService.GetFleetReportAsync(from, to, cancellationToken);
        return View(report);
    }
}
