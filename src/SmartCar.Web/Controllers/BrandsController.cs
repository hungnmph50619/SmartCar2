using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Brands;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class BrandsController : Controller
{
    private readonly IBrandService _brandService;

    public BrandsController(IBrandService brandService)
    {
        _brandService = brandService;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _brandService.GetAllAsync(cancellationToken));
    }

    [HttpGet]
    public IActionResult Create() => View(new BrandFormViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        BrandFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _brandService.CreateAsync(model.BrandName, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result.Errors);
            return View(model);
        }

        TempData["SuccessMessage"] = "Đã thêm hãng xe.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var brand = (await _brandService.GetAllAsync(cancellationToken))
            .FirstOrDefault(item => item.BrandId == id);

        if (brand is null)
        {
            return NotFound();
        }

        return View(new BrandFormViewModel
        {
            BrandId = brand.BrandId,
            BrandName = brand.BrandName
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        BrandFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _brandService.UpdateAsync(
            model.BrandId,
            model.BrandName,
            cancellationToken);

        if (!result.Succeeded)
        {
            AddErrors(result.Errors);
            return View(model);
        }

        TempData["SuccessMessage"] = "Đã cập nhật hãng xe.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(
        int id,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var result = await _brandService.SetActiveAsync(id, isActive, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã cập nhật trạng thái hãng xe."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    private void AddErrors(IEnumerable<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
