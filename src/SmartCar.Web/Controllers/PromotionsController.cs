using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Promotions;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

public sealed class PromotionsController : Controller
{
    private readonly IPromotionService _promotionService;

    public PromotionsController(IPromotionService promotionService)
    {
        _promotionService = promotionService;
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _promotionService.GetAllAsync(cancellationToken));
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpGet]
    public IActionResult Create() => View(new PromotionViewModel());

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        PromotionViewModel form,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(form);
        }

        var result = await _promotionService.CreateAsync(
            new SavePromotionRequest(
                form.Code,
                form.Name,
                form.PromotionType,
                form.Value,
                form.MaximumDiscount,
                form.MinimumRentalAmount,
                form.StartAt,
                form.EndAt,
                form.UsageLimit,
                form.IsActive),
            GetUserId(),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(form);
        }

        TempData["SuccessMessage"] = "Đã tạo mã khuyến mãi.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(
        int id,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var result = await _promotionService.ChangeStatusAsync(
            id,
            isActive,
            GetUserId(),
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã cập nhật trạng thái mã khuyến mãi."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(
        ApplyPromotionViewModel form,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _promotionService.ApplyToBookingAsync(
                form.BookingId,
                GetUserId(),
                form.Code,
                cancellationToken)
            : PromotionApplyResult.Failure("Mã khuyến mãi không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? $"Đã áp dụng mã. Bạn được giảm {result.DiscountAmount:N0} đồng."
            : string.Join("; ", result.Errors);
        return RedirectToAction("Details", "Bookings", new { id = form.BookingId });
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var result = await _promotionService.RemoveFromBookingAsync(
            bookingId,
            GetUserId(),
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã gỡ mã khuyến mãi."
            : string.Join("; ", result.Errors);
        return RedirectToAction("Details", "Bookings", new { id = bookingId });
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
}
