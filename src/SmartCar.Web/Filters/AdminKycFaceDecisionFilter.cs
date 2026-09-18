using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Filters;

public sealed class AdminKycFaceDecisionFilter : IAsyncActionFilter
{
    private static readonly string[] RequiredKycTypes =
    {
        DocumentTypes.CitizenId,
        DocumentTypes.CitizenIdBack,
        DocumentTypes.DrivingLicense,
        DocumentTypes.DrivingLicenseBack
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _storage;

    public AdminKycFaceDecisionFilter(
        ApplicationDbContext dbContext,
        ISecureDocumentStorage storage)
    {
        _dbContext = dbContext;
        _storage = storage;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor ||
            !string.Equals(descriptor.ControllerName, "AdminKyc", StringComparison.Ordinal))
        {
            await next();
            return;
        }

        var actionName = descriptor.ActionName;
        if (actionName is not ("VerifyAll" or "RequestResubmission"))
        {
            await next();
            return;
        }

        var customerId = context.ActionArguments.TryGetValue("customerId", out var customerValue)
            ? customerValue?.ToString()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(customerId))
        {
            await next();
            return;
        }

        if (actionName == "VerifyAll")
        {
            var hasFaceCandidate = await _dbContext.Users
                .AsNoTracking()
                .AnyAsync(user =>
                    user.Id == customerId &&
                    user.IdentityFaceImagePath != null &&
                    user.IdentityFaceImagePath != "" &&
                    user.IdentityFaceCapturedAt.HasValue,
                    context.HttpContext.RequestAborted);

            if (!hasFaceCandidate)
            {
                if (context.Controller is Controller controller)
                {
                    controller.TempData["ErrorMessage"] =
                        "Không thể duyệt KYC khi chưa có ảnh khuôn mặt chụp trực tiếp của khách. Yêu cầu khách hoàn tất bước camera/QR trước.";
                }

                context.Result = new RedirectToActionResult(
                    "Review",
                    "AdminKyc",
                    new { customerId });
                return;
            }
        }

        await next();

        if (actionName != "RequestResubmission")
        {
            return;
        }

        // Chỉ reset face khi action thực sự đã chuyển đủ 4 giấy tờ sang Rejected.
        // Nếu action thất bại giữa chừng thì giữ ảnh hiện tại để không làm mất dữ liệu vô cớ.
        var rejectedTypes = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == customerId &&
                document.Status == DocumentStatus.Rejected &&
                RequiredKycTypes.Contains(document.DocumentType))
            .Select(document => document.DocumentType)
            .Distinct()
            .ToListAsync(context.HttpContext.RequestAborted);

        if (!RequiredKycTypes.All(rejectedTypes.Contains))
        {
            return;
        }

        var customer = await _dbContext.Users
            .FirstOrDefaultAsync(
                user => user.Id == customerId,
                context.HttpContext.RequestAborted);
        if (customer is null || string.IsNullOrWhiteSpace(customer.IdentityFaceImagePath))
        {
            return;
        }

        var oldPath = customer.IdentityFaceImagePath;
        customer.IdentityFaceImagePath = null;
        customer.IdentityFaceCapturedAt = null;
        customer.IdentityFaceCaptureMethod = null;
        await _dbContext.SaveChangesAsync(context.HttpContext.RequestAborted);
        _storage.Delete(oldPath);
    }
}
