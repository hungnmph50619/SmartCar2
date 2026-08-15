using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnEvidenceController : Controller
{
    private const string SignatureResolvedAction = "ReturnDocumentSignatureDisputeResolved";

    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public ReturnEvidenceController(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveSignatureDispute(
        int bookingId,
        string resolutionNote,
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

        if (!model.SignatureDisputeActive)
        {
            TempData["ErrorMessage"] = "Hồ sơ này hiện không có tranh chấp chữ ký biên bản trả xe cần xử lý.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var note = resolutionNote?.Trim() ?? string.Empty;
        if (note.Length < 10)
        {
            TempData["ErrorMessage"] = "Vui lòng ghi rõ căn cứ xử lý tranh chấp, tối thiểu 10 ký tự.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        if (note.Length > 1000)
        {
            TempData["ErrorMessage"] = "Căn cứ xử lý tranh chấp tối đa 1000 ký tự.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            SignatureResolvedAction,
            nameof(Booking),
            bookingId.ToString(),
            $"Admin đã xử lý việc khách từ chối ký biên bản trả xe. Căn cứ/kết luận: {note}",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        var customerId = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.BookingId == bookingId)
            .Select(booking => booking.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = customerId,
                Title = "SmartCar đã xử lý hồ sơ biên bản trả xe",
                Message = $"Đơn #{bookingId}: SmartCar đã ghi nhận kết quả xử lý việc bạn từ chối ký biên bản trả xe. Hồ sơ ảnh và biên bản vẫn được lưu làm căn cứ; đơn tiếp tục bước kiểm tra và quyết toán theo kết quả xử lý."
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData["SuccessMessage"] = "Đã ghi nhận kết quả xử lý tranh chấp. Admin có thể quay lại đơn để tiếp tục kiểm tra và quyết toán cọc.";
        return RedirectToAction(nameof(Review), new { bookingId });
    }
}
