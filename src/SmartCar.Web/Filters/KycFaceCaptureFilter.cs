using System.Security.Claims;
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
        var customer = await dbContext.Users
            .FirstOrDefaultAsync(user => user.Id == customerId, context.HttpContext.RequestAborted);

        var validSession = session is not null &&
                           session.Purpose == IdentityCapturePurposes.Kyc &&
                           session.TargetCustomerId == customerId &&
                           session.CompletedAt.HasValue &&
                           !string.IsNullOrWhiteSpace(session.ImagePath) &&
                           IdentityCaptureMethods.All.Contains(
                               session.CaptureMethod ?? string.Empty,
                               StringComparer.Ordinal);

        if (!validSession || customer is null)
        {
            context.ModelState.AddModelError(
                nameof(KycPackageSubmitViewModel.FaceCaptureSessionId),
                customer is null
                    ? "Không tìm thấy tài khoản khách hàng."
                    : "Ảnh mặt KYC không hợp lệ, chưa chụp xong hoặc không thuộc tài khoản này. Vui lòng chụp lại.");
            await next();
            return;
        }

        // Nếu browser/server validation của chính form đã lỗi, giữ nguyên session chưa consume để
        // khách sửa text/ảnh giấy tờ rồi submit lại mà không phải chụp mặt lần nữa.
        if (!context.ModelState.IsValid)
        {
            await next();
            return;
        }

        if (session!.ConsumedAt.HasValue)
        {
            // Retry hợp lệ sau một lỗi nghiệp vụ phía action: session đã dùng một lần nhưng vẫn
            // được chấp nhận cho đúng tài khoản nếu ảnh ứng viên hiện tại chính là ảnh của session đó.
            var sameCandidate = string.Equals(
                customer.IdentityFaceImagePath,
                session.ImagePath,
                StringComparison.Ordinal);
            if (!sameCandidate)
            {
                context.ModelState.AddModelError(
                    nameof(KycPackageSubmitViewModel.FaceCaptureSessionId),
                    "Phiên ảnh mặt này đã được dùng cho hồ sơ khác hoặc ảnh ứng viên đã thay đổi. Vui lòng chụp lại.");
            }

            await next();
            return;
        }

        customer.IdentityFaceImagePath = session.ImagePath;
        customer.IdentityFaceCapturedAt = session.CompletedAt;
        customer.IdentityFaceCaptureMethod = session.CaptureMethod;
        session.ConsumedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(context.HttpContext.RequestAborted);

        await next();
    }
}
