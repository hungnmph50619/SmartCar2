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
        DateTime? fromDate,
        DateTime? toDate,
        string? paymentMethod,
        CancellationToken cancellationToken)
    {
        // Báo cáo dùng ngày nghiệp vụ Việt Nam. PaidAt trong DB vẫn lưu UTC.
        var vietnamToday = DateTime.UtcNow.AddHours(7).Date;
        var to = (toDate ?? vietnamToday).Date;
        var from = (fromDate ?? to.AddDays(-29)).Date;
        if (from > to)
        {
            (from, to) = (to, from);
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

        var report = await _reportService.GetFleetReportAsync(from, to, cancellationToken);
        return View(report);
    }
}
