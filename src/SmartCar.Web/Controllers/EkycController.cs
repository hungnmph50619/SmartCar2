using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class EkycController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;
    private const long MaximumSelfieVideoBytes = 20 * 1024 * 1024;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEkycService _ekycService;
    private readonly IEkycResultStore _ekycResultStore;
    private readonly IDocumentService _documentService;
    private readonly ISecureDocumentStorage _documentStorage;
    private readonly IAuditService _auditService;

    public EkycController(
        UserManager<ApplicationUser> userManager,
        IEkycService ekycService,
        IEkycResultStore ekycResultStore,
        IDocumentService documentService,
        ISecureDocumentStorage documentStorage,
        IAuditService auditService)
    {
        _userManager = userManager;
        _ekycService = ekycService;
        _ekycResultStore = ekycResultStore;
        _documentService = documentService;
        _documentStorage = documentStorage;
        _auditService = auditService;
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var latest = await _ekycResultStore.GetLatestSummaryAsync(user.Id, cancellationToken);
        return Json(new
        {
            provider = _ekycService.ProviderLabel,
            isDemo = _ekycService.IsDemoMode,
            latest
        });
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewCitizenId(
        IFormFile? frontImage,
        IFormFile? backImage,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var imageErrors = await ValidateDocumentImagesAsync(frontImage, backImage, cancellationToken);
        if (imageErrors.Count > 0 || frontImage is null || backImage is null)
        {
            return BadRequest(new { succeeded = false, errors = imageErrors });
        }

        var result = await _ekycService.ReadCitizenIdAsync(frontImage, backImage, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { succeeded = false, errors = new[] { result.Message } });
        }

        await _ekycResultStore.SaveOcrAsync(user.Id, result, cancellationToken);

        return Json(ToOcrResponse(result));
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewDrivingLicense(
        IFormFile? frontImage,
        IFormFile? backImage,
        CancellationToken cancellationToken)
    {
        var imageErrors = await ValidateDocumentImagesAsync(frontImage, backImage, cancellationToken);
        if (imageErrors.Count > 0 || frontImage is null || backImage is null)
        {
            return BadRequest(new { succeeded = false, errors = imageErrors });
        }

        var result = await _ekycService.ReadDrivingLicenseAsync(frontImage, backImage, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { succeeded = false, errors = new[] { result.Message } });
        }

        return Json(ToOcrResponse(result));
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyAndSubmitCitizenId(
        [Bind(Prefix = "CitizenIdVerification")] CitizenIdVerificationViewModel model,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        await ValidateCitizenIdAsync(model, returnDate, cancellationToken);
        if (!ModelState.IsValid || model.FrontImage is null || model.BackImage is null)
        {
            return BadRequest(new { succeeded = false, errors = CollectModelErrors() });
        }

        if (string.IsNullOrWhiteSpace(model.EkycSessionId))
        {
            return BadRequest(new
            {
                succeeded = false,
                errors = new[] { "Phiên eKYC không còn hiệu lực. Vui lòng bấm Đọc CCCD tự động lại." }
            });
        }

        var ocr = await _ekycResultStore.GetOcrAsync(
            user.Id,
            model.EkycSessionId,
            cancellationToken);
        if (ocr is null || !ocr.Succeeded)
        {
            return BadRequest(new
            {
                succeeded = false,
                errors = new[] { "Không tìm thấy kết quả OCR của phiên eKYC. Vui lòng đọc lại CCCD." }
            });
        }

        if (!ocr.IsDemo)
        {
            var mismatchErrors = CompareWithOcr(model, ocr);
            if (mismatchErrors.Count > 0)
            {
                return BadRequest(new { succeeded = false, errors = mismatchErrors });
            }
        }

        if (model.SelfieVideo is null)
        {
            return BadRequest(new
            {
                succeeded = false,
                errors = new[] { "Vui lòng quay video khuôn mặt để kiểm tra người thật và đối chiếu với CCCD." }
            });
        }

        var videoError = ValidateSelfieVideo(model.SelfieVideo);
        if (videoError is not null)
        {
            return BadRequest(new { succeeded = false, errors = new[] { videoError } });
        }

        var faceResult = await _ekycService.VerifyFaceAsync(
            model.EkycSessionId,
            model.SelfieVideo,
            cancellationToken);
        if (!faceResult.Succeeded)
        {
            await _ekycResultStore.SaveLatestSummaryAsync(
                user.Id,
                BuildSummary(ocr, faceResult, true),
                cancellationToken);

            return BadRequest(new
            {
                succeeded = false,
                errors = new[] { faceResult.Message }
            });
        }

        var existingDocuments = await _documentService.GetCustomerDocumentsAsync(user.Id, cancellationToken);
        var existingFront = existingDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var existingBack = existingDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);

        string? newFrontPath = null;
        string? newBackPath = null;

        try
        {
            newFrontPath = await _documentStorage.SaveAsync(model.FrontImage, user.Id, cancellationToken);
            newBackPath = await _documentStorage.SaveAsync(model.BackImage, user.Id, cancellationToken);

            var result = await _documentService.SubmitCitizenIdAsync(
                user.Id,
                new SubmitCitizenIdRequest(
                    model.FullNameOnDocument,
                    model.DocumentNumber,
                    model.DateOfBirth!.Value,
                    model.Gender,
                    model.IssuedDate!.Value,
                    model.ExpiryDate!.Value,
                    model.PermanentAddress,
                    newFrontPath,
                    newBackPath),
                cancellationToken);

            if (!result.Succeeded)
            {
                _documentStorage.Delete(newFrontPath);
                _documentStorage.Delete(newBackPath);
                return BadRequest(new { succeeded = false, errors = result.Errors });
            }

            DeleteReplacedImage(existingFront?.ImagePath, newFrontPath);
            DeleteReplacedImage(existingBack?.ImagePath, newBackPath);

            var summary = BuildSummary(
                ocr,
                faceResult,
                requiresManualReview: true);
            await _ekycResultStore.SaveLatestSummaryAsync(user.Id, summary, cancellationToken);

            await _auditService.WriteAsync(
                user.Id,
                "EkycCitizenIdSubmitted",
                "CustomerKyc",
                user.Id,
                $"{summary.Provider}; OCR={(summary.OcrSucceeded ? "pass" : "fail")}; " +
                $"Liveness={summary.LivenessPassed}; FaceMatch={summary.FaceMatched}; " +
                $"Similarity={summary.FaceSimilarity?.ToString("0.##") ?? "N/A"}; Demo={summary.IsDemo}. " +
                "Hồ sơ vẫn chờ Quản trị viên duyệt.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            var redirectUrl = Url.Action("Index", "Profile", new
            {
                tab = "documents",
                returnVehicleId,
                pickupDate,
                returnDate
            }) ?? "/Profile?tab=documents";

            return Json(new
            {
                succeeded = true,
                redirectUrl,
                message = summary.IsDemo
                    ? "Đã hoàn tất luồng eKYC demo và gửi hồ sơ chờ Quản trị viên duyệt."
                    : "Đã kiểm tra OCR, người thật và khuôn mặt. Hồ sơ đang chờ Quản trị viên duyệt lần cuối."
            });
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(newFrontPath))
            {
                _documentStorage.Delete(newFrontPath);
            }
            if (!string.IsNullOrWhiteSpace(newBackPath))
            {
                _documentStorage.Delete(newBackPath);
            }
            throw;
        }
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpGet]
    public async Task<IActionResult> AdminSummary(
        string customerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return BadRequest();
        }

        var result = await _ekycResultStore.GetLatestSummaryAsync(customerId, cancellationToken);
        return Json(new { result });
    }

    private async Task ValidateCitizenIdAsync(
        CitizenIdVerificationViewModel model,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var imageErrors = await ValidateDocumentImagesAsync(
            model.FrontImage,
            model.BackImage,
            cancellationToken);
        foreach (var error in imageErrors)
        {
            ModelState.AddModelError(string.Empty, error);
        }

        if (model.DateOfBirth.HasValue && model.DateOfBirth.Value.Date > DateTime.Today.AddYears(-18))
        {
            ModelState.AddModelError(
                "CitizenIdVerification.DateOfBirth",
                "Khách thuê xe phải đủ 18 tuổi.");
        }

        if (model.IssuedDate.HasValue && model.IssuedDate.Value.Date > DateTime.Today)
        {
            ModelState.AddModelError(
                "CitizenIdVerification.IssuedDate",
                "Ngày cấp không được sau ngày hiện tại.");
        }

        if (model.ExpiryDate.HasValue && model.ExpiryDate.Value.Date < DateTime.Today)
        {
            ModelState.AddModelError(
                "CitizenIdVerification.ExpiryDate",
                "CCCD đã hết hạn.");
        }

        if (model.IssuedDate.HasValue && model.ExpiryDate.HasValue &&
            model.ExpiryDate.Value.Date <= model.IssuedDate.Value.Date)
        {
            ModelState.AddModelError(
                "CitizenIdVerification.ExpiryDate",
                "Ngày hết hạn phải sau ngày cấp.");
        }

        if (returnDate.HasValue && model.ExpiryDate.HasValue &&
            model.ExpiryDate.Value.Date < returnDate.Value.Date)
        {
            ModelState.AddModelError(
                "CitizenIdVerification.ExpiryDate",
                $"CCCD phải còn hiệu lực ít nhất đến ngày trả xe {returnDate.Value:dd/MM/yyyy}.");
        }
    }

    private static async Task<List<string>> ValidateDocumentImagesAsync(
        IFormFile? frontImage,
        IFormFile? backImage,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var frontError = await ImageFileValidator.ValidateAsync(
            frontImage,
            MaximumDocumentImageBytes,
            cancellationToken);
        if (frontError is not null)
        {
            errors.Add($"Mặt trước: {frontError}");
        }

        var backError = await ImageFileValidator.ValidateAsync(
            backImage,
            MaximumDocumentImageBytes,
            cancellationToken);
        if (backError is not null)
        {
            errors.Add($"Mặt sau: {backError}");
        }

        return errors;
    }

    private static string? ValidateSelfieVideo(IFormFile video)
    {
        if (video.Length <= 0)
        {
            return "Video khuôn mặt trống.";
        }

        if (video.Length > MaximumSelfieVideoBytes)
        {
            return "Video khuôn mặt vượt quá 20 MB. Vui lòng quay video ngắn khoảng 3–5 giây.";
        }

        var extension = Path.GetExtension(video.FileName).ToLowerInvariant();
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".webm", ".mp4", ".mov", ".m4v"
        };

        var contentType = video.ContentType ?? string.Empty;
        if (!allowedExtensions.Contains(extension) ||
            (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
        {
            return "Video khuôn mặt phải là WEBM, MP4 hoặc MOV.";
        }

        return null;
    }

    private static List<string> CompareWithOcr(
        CitizenIdVerificationViewModel model,
        EkycOcrResult ocr)
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(ocr.DocumentNumber) &&
            !string.Equals(
                DigitsOnly(model.DocumentNumber),
                DigitsOnly(ocr.DocumentNumber),
                StringComparison.Ordinal))
        {
            errors.Add("Số CCCD đã nhập không khớp với kết quả OCR. Vui lòng bấm Đọc CCCD tự động lại hoặc chuyển sang xác minh thủ công.");
        }

        if (!string.IsNullOrWhiteSpace(ocr.FullName) &&
            !string.Equals(NormalizeText(model.FullNameOnDocument), NormalizeText(ocr.FullName), StringComparison.Ordinal))
        {
            errors.Add("Họ tên đã nhập không khớp với kết quả OCR trên CCCD.");
        }

        if (ocr.DateOfBirth.HasValue && model.DateOfBirth.HasValue &&
            ocr.DateOfBirth.Value.Date != model.DateOfBirth.Value.Date)
        {
            errors.Add("Ngày sinh không khớp với kết quả OCR trên CCCD.");
        }

        if (!string.IsNullOrWhiteSpace(ocr.Gender) &&
            !string.Equals(NormalizeText(model.Gender), NormalizeText(ocr.Gender), StringComparison.Ordinal))
        {
            errors.Add("Giới tính không khớp với kết quả OCR trên CCCD.");
        }

        if (ocr.IssuedDate.HasValue && model.IssuedDate.HasValue &&
            ocr.IssuedDate.Value.Date != model.IssuedDate.Value.Date)
        {
            errors.Add("Ngày cấp không khớp với kết quả OCR trên CCCD.");
        }

        if (ocr.ExpiryDate.HasValue && model.ExpiryDate.HasValue &&
            ocr.ExpiryDate.Value.Date != model.ExpiryDate.Value.Date)
        {
            errors.Add("Ngày hết hạn không khớp với kết quả OCR trên CCCD.");
        }

        return errors;
    }

    private static EkycVerificationSummary BuildSummary(
        EkycOcrResult ocr,
        EkycFaceVerificationResult face,
        bool requiresManualReview) =>
        new(
            face.Provider,
            ocr.IsDemo || face.IsDemo,
            ocr.Succeeded,
            ocr.OcrConfidence,
            face.IsLive,
            face.FaceMatched,
            face.Similarity,
            requiresManualReview,
            face.Message,
            face.CheckedAtUtc);

    private static object ToOcrResponse(EkycOcrResult result) => new
    {
        succeeded = true,
        result.IsDemo,
        result.Provider,
        result.SessionId,
        result.DocumentNumber,
        result.FullName,
        dateOfBirth = result.DateOfBirth?.ToString("dd/MM/yyyy"),
        result.Gender,
        issuedDate = result.IssuedDate?.ToString("dd/MM/yyyy"),
        expiryDate = result.ExpiryDate?.ToString("dd/MM/yyyy"),
        result.Address,
        result.LicenseClass,
        result.OcrConfidence,
        result.Message
    };

    private string[] CollectModelErrors() =>
        ModelState.Values
            .SelectMany(value => value.Errors)
            .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                ? "Dữ liệu xác minh không hợp lệ."
                : error.ErrorMessage)
            .Distinct()
            .ToArray();

    private void DeleteReplacedImage(string? oldPath, string newPath)
    {
        if (!string.IsNullOrWhiteSpace(oldPath) &&
            !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            _documentStorage.Delete(oldPath);
        }
    }

    private static string DigitsOnly(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder();
        var previousWasSpace = false;

        foreach (var character in value.Trim().ToUpperInvariant())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                builder.Append(character);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }
}
