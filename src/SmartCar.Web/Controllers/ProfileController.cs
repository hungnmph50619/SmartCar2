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
        model.Documents = await LoadDocumentsForCurrentRoleAsync(
            user.Id,
            cancellationToken);

        if (!ModelState.IsValid)
        {
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
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitDocument(
        [Bind(Prefix = "DocumentUpload")] DocumentUploadViewModel model,
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

        var imageError = await ImageFileValidator.ValidateAsync(
            model.Image,
            MaximumDocumentImageBytes,
            cancellationToken);
        if (imageError is not null)
        {
            ModelState.AddModelError("DocumentUpload.Image", imageError);
        }

        if (!ModelState.IsValid || model.Image is null)
        {
            await SetRentalReturnContextAsync(
                returnVehicleId,
                pickupDate,
                returnDate,
                cancellationToken);

            var pageModel = await BuildViewModelAsync(
                user,
                "documents",
                cancellationToken);
            pageModel.DocumentUpload = model;
            return View("Index", pageModel);
        }

        var existingDocuments = await _documentService.GetCustomerDocumentsAsync(
            user.Id,
            cancellationToken);
        var existingDocument = existingDocuments.FirstOrDefault(item =>
            item.DocumentType == model.DocumentType);

        var newStoredPath = await _documentStorage.SaveAsync(
            model.Image,
            user.Id,
            cancellationToken);

        var result = await _documentService.SubmitAsync(
            user.Id,
            new SubmitDocumentRequest(
                model.DocumentType,
                model.DocumentNumber,
                model.ExpiryDate,
                newStoredPath),
            cancellationToken);

        if (!result.Succeeded)
        {
            _documentStorage.Delete(newStoredPath);
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
        }
        else
        {
            if (existingDocument is not null &&
                !string.Equals(
                    existingDocument.ImagePath,
                    newStoredPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _documentStorage.Delete(existingDocument.ImagePath);
            }

            TempData["SuccessMessage"] =
                "Đã gửi giấy tờ thành công. Hồ sơ đang chờ Quản trị viên xác minh.";
        }

        return RedirectToAction(nameof(Index), new
        {
            tab = "documents",
            returnVehicleId,
            pickupDate,
            returnDate
        });
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

        var vehicle = await _vehicleService.GetByIdAsync(
            returnVehicleId.Value,
            cancellationToken);
        ViewBag.ReturnVehicleName = vehicle?.VehicleName;
    }

    private async Task<ProfileViewModel> BuildViewModelAsync(
        ApplicationUser user,
        string activeTab,
        CancellationToken cancellationToken) => new()
    {
        FullName = user.FullName,
        Email = user.Email ?? string.Empty,
        PhoneNumber = user.PhoneNumber ?? string.Empty,
        Address = user.Address ?? string.Empty,
        CreatedAt = user.CreatedAt,
        ActiveTab = activeTab,
        Documents = await LoadDocumentsForCurrentRoleAsync(user.Id, cancellationToken)
    };

    private async Task<IReadOnlyList<DocumentDto>> LoadDocumentsForCurrentRoleAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole(RoleNames.Customer))
        {
            return Array.Empty<DocumentDto>();
        }

        return await _documentService.GetCustomerDocumentsAsync(
            userId,
            cancellationToken);
    }
}
