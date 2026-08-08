using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Filters;

public sealed class DuplicateDocumentImagesFilter : IAsyncActionFilter
{
    private const string DuplicateMessage =
        "Mặt trước và mặt sau không được dùng cùng một ảnh. Vui lòng chọn đúng hai mặt của giấy tờ.";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var cancellationToken = context.HttpContext.RequestAborted;

        foreach (var argument in context.ActionArguments.Values)
        {
            switch (argument)
            {
                case CitizenIdVerificationViewModel citizenId
                    when await ImageFileValidator.HaveSameContentAsync(
                        citizenId.FrontImage,
                        citizenId.BackImage,
                        cancellationToken):
                    context.ModelState.AddModelError(
                        "CitizenIdVerification.BackImage",
                        DuplicateMessage);
                    break;

                case DrivingLicenseVerificationViewModel drivingLicense
                    when await ImageFileValidator.HaveSameContentAsync(
                        drivingLicense.FrontImage,
                        drivingLicense.BackImage,
                        cancellationToken):
                    context.ModelState.AddModelError(
                        "DrivingLicenseVerification.BackImage",
                        DuplicateMessage);
                    break;
            }
        }

        if (context.ActionArguments.TryGetValue("frontImage", out var frontValue) &&
            context.ActionArguments.TryGetValue("backImage", out var backValue) &&
            frontValue is IFormFile frontImage &&
            backValue is IFormFile backImage &&
            await ImageFileValidator.HaveSameContentAsync(frontImage, backImage, cancellationToken))
        {
            context.Result = new BadRequestObjectResult(new
            {
                succeeded = false,
                errors = new[] { DuplicateMessage }
            });
            return;
        }

        await next();
    }
}
