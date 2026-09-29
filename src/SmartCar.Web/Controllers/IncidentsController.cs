using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Incidents;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin + "," + RoleNames.Staff)]
public sealed class IncidentsController : Controller
{
    private const int MaximumDamageImages = 5;
    private const long MaximumImageBytes = 5 * 1024 * 1024;
    private readonly IIncidentService _incidentService;
    private readonly IVehicleService _vehicleService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    public IncidentsController(
        IIncidentService incidentService,
        IVehicleService vehicleService,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _incidentService = incidentService;
        _vehicleService = vehicleService;
        _dbContext = dbContext;
        _environment = environment;
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpGet]
    public async Task<IActionResult> Index(
        IncidentStatus? status,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        return View(await _incidentService.GetAllAsync(status, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Create(
        int? vehicleId,
        int? bookingId,
        CancellationToken cancellationToken)
    {
        if (User.IsInRole(RoleNames.Staff) && !User.IsInRole(RoleNames.Admin) && !bookingId.HasValue)
        {
            return RedirectToAction("Bookings", "Staff");
        }

        var relatedBooking = bookingId.HasValue
            ? await _dbContext.Bookings.AsNoTracking()
                .Include(booking => booking.VehicleReturn)
                .FirstOrDefaultAsync(booking => booking.BookingId == bookingId.Value, cancellationToken)
            : null;
        if (bookingId.HasValue && relatedBooking is null)
        {
            return NotFound();
        }

        await LoadVehiclesAsync(vehicleId, cancellationToken);
        return View(new IncidentCreateViewModel
        {
            VehicleId = relatedBooking?.VehicleId ?? vehicleId ?? 0,
            BookingId = bookingId,
            IncidentType = User.IsInRole(RoleNames.Staff) || vehicleId.HasValue
                ? IncidentType.Breakdown : IncidentType.Accident,
            OccurredAt = relatedBooking?.VehicleReturn?.ReturnedAt ?? DateTime.Now
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        IncidentCreateViewModel form,
        CancellationToken cancellationToken)
    {
        var isStaff = User.IsInRole(RoleNames.Staff) && !User.IsInRole(RoleNames.Admin);
        var images = (form.Images ?? new List<IFormFile>())
            .Where(file => file.Length > 0).ToList();
        if (isStaff)
        {
            if (!form.BookingId.HasValue || !await _dbContext.Bookings.AnyAsync(booking =>
                    booking.BookingId == form.BookingId.Value &&
                    booking.VehicleId == form.VehicleId &&
                    booking.VehicleReturn != null, cancellationToken))
            {
                ModelState.AddModelError(nameof(form.BookingId),
                    "Nhân viên phải gắn xe hỏng với đơn đã có biên bản trả.");
            }
            if (images.Count == 0)
            {
                ModelState.AddModelError(nameof(form.Images), "Vui lòng tải ít nhất một ảnh hỏng hóc.");
            }

            form.IncidentType = IncidentType.Breakdown;
            form.EstimatedCost = 0m;
            form.FineAmount = 0m;
            form.CustomerLiabilityAmount = 0m;
            form.EvidencePaths = null;
        }

        if (images.Count > MaximumDamageImages)
        {
            ModelState.AddModelError(nameof(form.Images), "Chỉ được tải tối đa 5 ảnh.");
        }
        foreach (var image in images)
        {
            var error = await ImageFileValidator.ValidateAsync(image, MaximumImageBytes, cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(form.Images), error);
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadVehiclesAsync(form.VehicleId, cancellationToken);
            return View(form);
        }

        var savedPaths = new List<string>();
        try
        {
            foreach (var image in images)
            {
                var relativeFolder = $"uploads/incidents/{form.VehicleId}";
                var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
                Directory.CreateDirectory(folder);
                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(image.FileName).ToLowerInvariant()}";
                var fullPath = Path.Combine(folder, fileName);
                await using var stream = System.IO.File.Create(fullPath);
                await image.CopyToAsync(stream, cancellationToken);
                savedPaths.Add($"/{relativeFolder}/{fileName}");
            }

            var evidencePaths = string.Join(";",
                new[] { form.EvidencePaths }.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Concat(savedPaths));
            var result = await _incidentService.CreateAsync(
                new CreateIncidentRequest(
                    form.VehicleId,
                    form.BookingId,
                    form.IncidentType,
                    form.OccurredAt,
                    form.Location,
                    form.Description,
                    form.EstimatedCost,
                    form.FineAmount,
                    form.CustomerLiabilityAmount,
                    evidencePaths,
                    form.Notes),
                GetUserId(),
                cancellationToken);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error);
                }

                await LoadVehiclesAsync(form.VehicleId, cancellationToken);
                return View(form);
            }

            savedPaths.Clear();
        }
        finally
        {
            foreach (var path in savedPaths)
            {
                var fullPath = Path.Combine(_environment.WebRootPath, path.TrimStart('/'));
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            }
        }

        TempData["SuccessMessage"] = "Đã ghi nhận sự cố hoặc vi phạm.";
        return isStaff
            ? RedirectToAction("Details", "Staff", new { id = form.BookingId })
            : RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartInvestigation(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await _incidentService.StartInvestigationAsync(
            id,
            GetUserId(),
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã chuyển sự cố sang trạng thái đang xử lý."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(
        IncidentResolveViewModel form,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _incidentService.ResolveAsync(
                new ResolveIncidentRequest(
                    form.IncidentId,
                    form.ActualCost,
                    form.FineAmount,
                    form.CustomerLiabilityAmount,
                    form.Notes),
                GetUserId(),
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Dữ liệu xử lý sự cố không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã hoàn tất xử lý sự cố."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private async Task LoadVehiclesAsync(
        int? selectedId,
        CancellationToken cancellationToken)
    {
        ViewBag.Vehicles = new SelectList(
            await _vehicleService.GetAllAsync(cancellationToken),
            "VehicleId",
            "VehicleName",
            selectedId);
    }
}
