using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Filters;

public sealed class KycFaceCaptureFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var kycModel = context.ActionArguments.Values
            .OfType<KycPackageSubmitViewModel>()
            .FirstOrDefault();
        if (kycModel is null)
        {
            await next();
            return;
        }

        var customerId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId) || !kycModel.FaceCaptureSessionId.HasValue)
        {
            context.ModelState.AddModelError(
                nameof(KycPackageSubmitViewModel.FaceCaptureSessionId),
                "Vui lòng chụp ảnh khuôn mặt trực tiếp trước khi gửi hồ sơ KYC.");
            await next();
            return;
        }

        var dbContext = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var session = await dbContext.Set<IdentityCaptureSession>()
            .FirstOrDefaultAsync(item =>
                item.IdentityCaptureSessionId == kycModel.FaceCaptureSessionId.Value,
                context.HttpContext.RequestAborted);

        var valid = session is not null &&
                    session.Purpose == IdentityCapturePurposes.Kyc &&
                    session.TargetCustomerId == customerId &&
                    session.CompletedAt.HasValue &&
                    !string.IsNullOrWhiteSpace(session.ImagePath) &&
                    IdentityCaptureMethods.All.Contains(session.CaptureMethod ?? string.Empty, StringComparer.Ordinal);

        if (!valid)
        {
            context.ModelState.AddModelError(
                nameof(KycPackageSubmitViewModel.FaceCaptureSessionId),
                "Ảnh mặt KYC không hợp lệ, chưa chụp xong hoặc không thuộc tài khoản này. Vui lòng chụp lại.");
            await next();
            return;
        }

        var customer = await dbContext.Users
            .FirstOrDefaultAsync(user => user.Id == customerId, context.HttpContext.RequestAborted);
        if (customer is null)
        {
            context.ModelState.AddModelError(string.Empty, "Không tìm thấy tài khoản khách hàng.");
            await next();
            return;
        }

        // Đây là ảnh ứng viên KYC; chỉ Admin mới biến bộ hồ sơ thành Verified.
        customer.IdentityFaceImagePath = session!.ImagePath;
        customer.IdentityFaceCapturedAt = session.CompletedAt;
        customer.IdentityFaceCaptureMethod = session.CaptureMethod;
        await dbContext.SaveChangesAsync(context.HttpContext.RequestAborted);

        await next();
    }
}
