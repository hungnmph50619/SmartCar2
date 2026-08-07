using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class ProfileController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _auditService;
    private readonly IDocumentService _documentService;
    private readonly ISecureDocumentStorage _documentStorage;
    private readonly IVehicleService _vehicleService;

    public ProfileController(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService,
        IDocumentService documentService,
        ISecureDocumentStorage documentStorage,
        IVehicleService vehicleService)
    {
        _userManager = userManager;
        _auditService = auditService;
        _documentService = documentService;
        _documentStorage = documentStorage;
        _vehicleService = vehicleService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? tab,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var activeTab = User.IsInRole(RoleNames.Customer) &&
                        string.Equals(tab, "documents", StringComparison.OrdinalIgnoreCase)
            ? "documents"
            : "profile";

        await SetRentalReturnContextAsync(
            returnVehicleId,
            pickupDate,
            returnDate,
            cancellationToken);

        return View(await BuildViewModelAsync(user, activeTab, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        ProfileViewModel model,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        model.Email = user.Email ?? string.Empty;
        model.CreatedAt = user.CreatedAt;
        model.ActiveTab = "profile";
        model.Documents = await LoadDocumentsForCurrentRoleAsync(user.Id, cancellationToken);

        if (!ModelState.IsValid)
        {
            await SetRentalReturnContextAsync(returnVehicleId, pickupDate, returnDate, cancellationToken);
            return View("Index", model);
        }

        user.FullName = model.FullName.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber)
            ? null
            : model.PhoneNumber.Trim();
        user.Address = string.IsNullOrWhiteSpace(model.Address)
            ? null
            : model.Address.Trim();

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await SetRentalReturnContextAsync(returnVehicleId, pickupDate, returnDate, cancellationToken);
            return View("Index", model);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            userId,
            "UpdateProfile",
            "UserProfile",
            userId ?? string.Empty,
            "Cập nhật họ tên, số điện thoại hoặc địa chỉ hồ sơ cá nhân.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật hồ sơ cá nhân.";
        return RedirectToAction(nameof(Index), new
        {
            tab = "profile",
            returnVehicleId,
            pickupDate,
            returnDate
        });
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCitizenId(
        [Bind(Prefix = "CitizenIdVerification")] CitizenIdVerificationViewModel model,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await ValidateCitizenIdFormAsync(model, returnDate, cancellationToken);
        if (!ModelState.IsValid || model.FrontImage is null || model.BackImage is null)
        {
            return await RenderInvalidKycAsync(
                user,
                model,
                null,
                returnVehicleId,
                pickupDate,
                returnDate,
                cancellationToken);
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
                TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            }
            else
            {
                DeleteReplacedImage(existingFront?.ImagePath, newFrontPath);
                DeleteReplacedImage(existingBack?.ImagePath, newBackPath);
                TempData["SuccessMessage"] =
                    "Đã gửi thông tin CCCD cùng ảnh mặt trước và mặt sau. Hồ sơ đang chờ Quản trị viên xác minh.";
            }
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

        return RedirectToKyc(returnVehicleId, pickupDate, returnDate);
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitDrivingLicense(
        [Bind(Prefix = "DrivingLicenseVerification")] DrivingLicenseVerificationViewModel model,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await ValidateDrivingLicenseFormAsync(model, returnDate, cancellationToken);
        if (!ModelState.IsValid || model.Image is null)
        {
            return await RenderInvalidKycAsync(
                user,
                null,
                model,
                returnVehicleId,
                pickupDate,
                returnDate,
                cancellationToken);
        }

        var existingDocuments = await _documentService.GetCustomerDocumentsAsync(user.Id, cancellationToken);
        var existingDocument = existingDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);

        var newStoredPath = await _documentStorage.SaveAsync(model.Image, user.Id, cancellationToken);
        var result = await _documentService.SubmitDrivingLicenseAsync(
            user.Id,
            new SubmitDrivingLicenseRequest(
                model.FullNameOnDocument,
                model.DocumentNumber,
                model.LicenseClass,
                model.IssuedDate!.Value,
                model.ExpiryDate!.Value,
                newStoredPath),
            cancellationToken);

        if (!result.Succeeded)
        {
            _documentStorage.Delete(newStoredPath);
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
        }
        else
        {
            DeleteReplacedImage(existingDocument?.ImagePath, newStoredPath);
            TempData["SuccessMessage"] =
                "Đã gửi đầy đủ thông tin và ảnh GPLX. Hồ sơ đang chờ Quản trị viên xác minh.";
        }

        return RedirectToKyc(returnVehicleId, pickupDate, returnDate);
    }

    private async Task ValidateCitizenIdFormAsync(
        CitizenIdVerificationViewModel model,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var frontImageError = await ImageFileValidator.ValidateAsync(
            model.FrontImage,
            MaximumDocumentImageBytes,
            cancellationToken);
        if (frontImageError is not null)
        {
            ModelState.AddModelError("CitizenIdVerification.FrontImage", frontImageError);
        }

        var backImageError = await ImageFileValidator.ValidateAsync(
            model.BackImage,
            MaximumDocumentImageBytes,
            cancellationToken);
        if (backImageError is not null)
        {
            ModelState.AddModelError("CitizenIdVerification.BackImage", backImageError);
        }

        if (model.DateOfBirth.HasValue && model.DateOfBirth.Value.Date > DateTime.Today.AddYears(-18))
        {
            ModelState.AddModelError("CitizenIdVerification.DateOfBirth", "Khách thuê xe phải đủ 18 tuổi.");
        }

        if (model.IssuedDate.HasValue && model.IssuedDate.Value.Date > DateTime.Today)
        {
            ModelState.AddModelError("CitizenIdVerification.IssuedDate", "Ngày cấp không được sau ngày hiện tại.");
        }

        if (model.ExpiryDate.HasValue && model.ExpiryDate.Value.Date < DateTime.Today)
        {
            ModelState.AddModelError("CitizenIdVerification.ExpiryDate", "CCCD đã hết hạn.");
        }

        if (model.IssuedDate.HasValue && model.ExpiryDate.HasValue &&
            model.ExpiryDate.Value.Date <= model.IssuedDate.Value.Date)
        {
            ModelState.AddModelError("CitizenIdVerification.ExpiryDate", "Ngày hết hạn phải sau ngày cấp.");
        }

        if (returnDate.HasValue && model.ExpiryDate.HasValue &&
            model.ExpiryDate.Value.Date < returnDate.Value.Date)
        {
            ModelState.AddModelError(
                "CitizenIdVerification.ExpiryDate",
                $"CCCD phải còn hiệu lực ít nhất đến ngày trả xe {returnDate.Value:dd/MM/yyyy}.");
        }
    }

    private async Task ValidateDrivingLicenseFormAsync(
        DrivingLicenseVerificationViewModel model,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var imageError = await ImageFileValidator.ValidateAsync(
            model.Image,
            MaximumDocumentImageBytes,
            cancellationToken);
        if (imageError is not null)
        {
            ModelState.AddModelError("DrivingLicenseVerification.Image", imageError);
        }

        if (model.IssuedDate.HasValue && model.IssuedDate.Value.Date > DateTime.Today)
        {
            ModelState.AddModelError("DrivingLicenseVerification.IssuedDate", "Ngày cấp không được sau ngày hiện tại.");
        }

        if (model.ExpiryDate.HasValue && model.ExpiryDate.Value.Date < DateTime.Today)
        {
            ModelState.AddModelError("DrivingLicenseVerification.ExpiryDate", "GPLX đã hết hạn.");
        }

        if (model.IssuedDate.HasValue && model.ExpiryDate.HasValue &&
            model.ExpiryDate.Value.Date <= model.IssuedDate.Value.Date)
        {
            ModelState.AddModelError("DrivingLicenseVerification.ExpiryDate", "Ngày hết hạn phải sau ngày cấp.");
        }

        if (returnDate.HasValue && model.ExpiryDate.HasValue &&
            model.ExpiryDate.Value.Date < returnDate.Value.Date)
        {
            ModelState.AddModelError(
                "DrivingLicenseVerification.ExpiryDate",
                $"GPLX phải còn hiệu lực ít nhất đến ngày trả xe {returnDate.Value:dd/MM/yyyy}.");
        }
    }

    private async Task<IActionResult> RenderInvalidKycAsync(
        ApplicationUser user,
        CitizenIdVerificationViewModel? citizenIdModel,
        DrivingLicenseVerificationViewModel? drivingLicenseModel,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        await SetRentalReturnContextAsync(returnVehicleId, pickupDate, returnDate, cancellationToken);
        var pageModel = await BuildViewModelAsync(user, "documents", cancellationToken);
        if (citizenIdModel is not null)
        {
            pageModel.CitizenIdVerification = citizenIdModel;
        }
        if (drivingLicenseModel is not null)
        {
            pageModel.DrivingLicenseVerification = drivingLicenseModel;
        }
        return View("Index", pageModel);
    }

    private IActionResult RedirectToKyc(
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate) =>
        RedirectToAction(nameof(Index), new
        {
            tab = "documents",
            returnVehicleId,
            pickupDate,
            returnDate
        });

    private void DeleteReplacedImage(string? oldPath, string newPath)
    {
        if (!string.IsNullOrWhiteSpace(oldPath) &&
            !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            _documentStorage.Delete(oldPath);
        }
    }

    private async Task SetRentalReturnContextAsync(
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        ViewBag.ReturnVehicleId = returnVehicleId;
        ViewBag.ReturnPickupDate = pickupDate;
        ViewBag.ReturnDate = returnDate;
        ViewBag.ReturnVehicleName = null;

        if (!returnVehicleId.HasValue)
        {
            return;
        }

        var vehicle = await _vehicleService.GetByIdAsync(returnVehicleId.Value, cancellationToken);
        ViewBag.ReturnVehicleName = vehicle?.VehicleName;
    }

    private async Task<ProfileViewModel> BuildViewModelAsync(
        ApplicationUser user,
        string activeTab,
        CancellationToken cancellationToken)
    {
        var documents = await LoadDocumentsForCurrentRoleAsync(user.Id, cancellationToken);
        var citizenId = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var drivingLicense = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);

        return new ProfileViewModel
        {
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            PhoneNumber = user.PhoneNumber ?? string.Empty,
            Address = user.Address ?? string.Empty,
            CreatedAt = user.CreatedAt,
            ActiveTab = activeTab,
            Documents = documents,
            CitizenIdVerification = new CitizenIdVerificationViewModel
            {
                FullNameOnDocument = citizenId?.FullNameOnDocument ?? user.FullName,
                DocumentNumber = citizenId?.DocumentNumber ?? string.Empty,
                DateOfBirth = citizenId?.DateOfBirth,
                Gender = citizenId?.Gender ?? string.Empty,
                IssuedDate = citizenId?.IssuedDate,
                ExpiryDate = citizenId?.ExpiryDate,
                PermanentAddress = citizenId?.PermanentAddress ?? user.Address ?? string.Empty
            },
            DrivingLicenseVerification = new DrivingLicenseVerificationViewModel
            {
                FullNameOnDocument = drivingLicense?.FullNameOnDocument ?? user.FullName,
                DocumentNumber = drivingLicense?.DocumentNumber ?? string.Empty,
                LicenseClass = drivingLicense?.LicenseClass ?? string.Empty,
                IssuedDate = drivingLicense?.IssuedDate,
                ExpiryDate = drivingLicense?.ExpiryDate
            }
        };
    }

    private async Task<IReadOnlyList<DocumentDto>> LoadDocumentsForCurrentRoleAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole(RoleNames.Customer))
        {
            return Array.Empty<DocumentDto>();
        }

        return await _documentService.GetCustomerDocumentsAsync(userId, cancellationToken);
    }
}
