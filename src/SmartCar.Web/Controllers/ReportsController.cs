using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Reports;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReportsController : Controller
{
    public const string UnknownPaymentMethodFilter = "__UNKNOWN__";

    private readonly IReportService _reportService;

    public ReportsController(IReportService reportService)
    {
        _reportService = reportService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? fromDate,
        string? toDate,
        string? paymentMethod,
        CancellationToken cancellationToken)
    {
        // Báo cáo dùng ngày nghiệp vụ Việt Nam. PaidAt trong DB vẫn lưu UTC.
        var vietnamToday = DateTime.UtcNow.AddHours(7).Date;
        ViewBag.Today = vietnamToday;
        // MVC converts empty query values to null; only a genuinely initial visit uses defaults.
        if (Request.Query.ContainsKey(nameof(fromDate)) || Request.Query.ContainsKey(nameof(toDate)))
        {
            fromDate ??= string.Empty;
            toDate ??= string.Empty;
        }
        if (!ReportDateRange.TryCreate(fromDate, toDate, vietnamToday, out var range, out var error))
        {
            ModelState.AddModelError(string.Empty, error!);
            ViewBag.FromDate = fromDate;
            ViewBag.ToDate = toDate;
            ViewBag.SelectedPaymentMethod = paymentMethod;
            return View("InvalidDates");
        }

        var allowedFilters = new[]
        {
            PaymentMethods.BankQr,
            PaymentMethods.Cash,
            PaymentMethods.DepositDeduction,
            UnknownPaymentMethodFilter
        };

        var selectedPaymentMethod = allowedFilters.Contains(paymentMethod)
            ? paymentMethod
            : null;

        ViewBag.SelectedPaymentMethod = selectedPaymentMethod;
        ViewBag.UnknownPaymentMethodFilter = UnknownPaymentMethodFilter;

        var report = await _reportService.GetFleetReportAsync(range!.From, range.To, cancellationToken);
        return View(report);
    }
}

