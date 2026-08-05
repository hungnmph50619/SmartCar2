using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Reviews;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class ReviewsController : Controller
{
    private readonly IReviewService _reviewService;

    public ReviewsController(IReviewService reviewService)
    {
        _reviewService = reviewService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ReviewFormViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var result = ModelState.IsValid
            ? await _reviewService.CreateAsync(
                model.BookingId,
                customerId,
                model.Rating,
                model.Comment,
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Nội dung đánh giá không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Cảm ơn bạn đã đánh giá chuyến thuê xe."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
    }
}
