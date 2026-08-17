using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class ExtensionsController : Controller
{
    private readonly IExtensionService _extensionService;

    public ExtensionsController(IExtensionService extensionService)
    {
        _extensionService = extensionService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        return View(await _extensionService.GetCustomerExtensionsAsync(
            customerId,
            cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitRequest(
        ExtensionRequestViewModel model,
        bool isForceMajeure,
        string? evidenceNote,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var result = ModelState.IsValid
            ? await _extensionService.RequestAsync(
                customerId,
                new RequestExtensionRequest(
                    model.BookingId,
                    model.RequestedReturnDate,
                    model.CustomerNote,
                    isForceMajeure,
                    evidenceNote),
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Thông tin gia hạn không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? isForceMajeure
                ? "Đã gửi yêu cầu gia hạn bất khả kháng. SmartCar sẽ kiểm tra minh chứng và lịch xe trước khi quyết định."
                : "Đã gửi yêu cầu gia hạn."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SupplementEvidence(
        int extensionId,
        int bookingId,
        string evidenceNote,
        string? customerNote,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var result = await _extensionService.SupplementEvidenceAsync(
            extensionId,
            customerId,
            evidenceNote,
            customerNote,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã bổ sung minh chứng và gửi lại yêu cầu gia hạn."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index));
    }
}
