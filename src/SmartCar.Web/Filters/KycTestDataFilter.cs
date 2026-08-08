using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartCar.Web.Services;

namespace SmartCar.Web.Filters;

/// <summary>
/// Returns deterministic sample extraction results only when the Development KYC test mode
/// has been explicitly enabled by the customer. Production requests never enter this path.
/// </summary>
public sealed class KycTestDataFilter : IAsyncActionFilter, IOrderedFilter
{
    private static readonly HashSet<string> CitizenPreviewActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "PreviewCitizenQrMrz",
        "PreviewCitizenId"
    };

    private static readonly HashSet<string> LicensePreviewActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "PreviewLicenseQr",
        "PreviewDrivingLicense"
    };

    private readonly KycTestingService _testing;
    private readonly IEkycResultStore _resultStore;

    public KycTestDataFilter(KycTestingService testing, IEkycResultStore resultStore)
    {
        _testing = testing;
        _resultStore = resultStore;
    }

    public int Order => -5000;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!_testing.IsActive(context.HttpContext))
        {
            await next();
            return;
        }

        var action = context.ActionDescriptor.RouteValues.TryGetValue("action", out var actionName)
            ? actionName
            : null;
        if (string.IsNullOrWhiteSpace(action))
        {
            await next();
            return;
        }

        if (CitizenPreviewActions.Contains(action))
        {
            var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            var sample = KycTestSamples.CreateCitizenOcrResult();
            await _resultStore.SaveOcrAsync(userId, sample, context.HttpContext.RequestAborted);
            context.Result = new JsonResult(new
            {
                succeeded = true,
                isDemo = true,
                provider = sample.Provider,
                sessionId = sample.SessionId,
                sample.DocumentNumber,
                sample.FullName,
                dateOfBirth = sample.DateOfBirth?.ToString("dd/MM/yyyy"),
                sample.Gender,
                issuedDate = sample.IssuedDate?.ToString("dd/MM/yyyy"),
                expiryDate = sample.ExpiryDate?.ToString("dd/MM/yyyy"),
                sample.Address,
                ocrConfidence = sample.OcrConfidence,
                sample.Message,
                qrDecoded = true,
                mrzMatched = true,
                testMode = true
            });
            return;
        }

        if (LicensePreviewActions.Contains(action))
        {
            context.Result = new JsonResult(new
            {
                succeeded = true,
                isDemo = true,
                provider = "SmartCar KYC Test Mode",
                qrDecoded = true,
                sourceSide = "sample",
                extractedCount = 5,
                fullName = KycTestSamples.CitizenFullName,
                documentNumber = KycTestSamples.LicenseDocumentNumber,
                licenseClass = KycTestSamples.LicenseClass,
                issuedDate = KycTestSamples.LicenseIssuedDate.ToString("dd/MM/yyyy"),
                expiryDate = KycTestSamples.LicenseExpiryDate.ToString("dd/MM/yyyy"),
                message = "Chế độ kiểm thử Development: dữ liệu GPLX mẫu được mô phỏng để kiểm tra luồng hệ thống.",
                testMode = true
            });
            return;
        }

        await next();
    }
}
