using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnEvidenceController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    public ReturnEvidenceController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Review(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var model = await ReturnEvidenceHelper.BuildAsync(
            _dbContext,
            bookingId,
            cancellationToken);

        if (model is null)
        {
            return NotFound();
        }

        return View(model);
    }
}
