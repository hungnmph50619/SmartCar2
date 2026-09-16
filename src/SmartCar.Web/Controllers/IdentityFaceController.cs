using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin + "," + RoleNames.Staff)]
public sealed class IdentityFaceController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _storage;

    public IdentityFaceController(
        ApplicationDbContext dbContext,
        ISecureDocumentStorage storage)
    {
        _dbContext = dbContext;
        _storage = storage;
    }

    [HttpGet]
    public async Task<IActionResult> CustomerKyc(
        string customerId,
        CancellationToken cancellationToken)
    {
        var path = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == customerId)
            .Select(user => user.IdentityFaceImagePath)
            .FirstOrDefaultAsync(cancellationToken);
        return Serve(path);
    }

    [HttpGet]
    public async Task<IActionResult> Handover(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var path = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => item.ReceiverFaceImagePath)
            .FirstOrDefaultAsync(cancellationToken);
        return Serve(path);
    }

    [HttpGet]
    public async Task<IActionResult> Return(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var path = await _dbContext.VehicleReturns
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => item.ReturnerFaceImagePath)
            .FirstOrDefaultAsync(cancellationToken);
        return Serve(path);
    }

    private IActionResult Serve(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) ||
            !_storage.TryResolve(storedPath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, contentType);
    }
}
