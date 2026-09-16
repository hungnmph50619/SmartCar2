using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminStaffPasswordController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _auditService;

    public AdminStaffPasswordController(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService)
    {
        _userManager = userManager;
        _auditService = auditService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTemporaryPassword(
        string id,
        string temporaryPassword,
        string confirmTemporaryPassword,
        CancellationToken cancellationToken)
    {
        var staff = await _userManager.FindByIdAsync(id);
        if (staff is null || !await _userManager.IsInRoleAsync(staff, RoleNames.Staff))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(temporaryPassword))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập mật khẩu tạm mới.";
            return Back(staff.Id);
        }

        if (!string.Equals(temporaryPassword, confirmTemporaryPassword, StringComparison.Ordinal))
        {
            TempData["ErrorMessage"] = "Mật khẩu xác nhận không khớp.";
            return Back(staff.Id);
        }

        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(staff);
        var resetResult = await _userManager.ResetPasswordAsync(staff, resetToken, temporaryPassword);
        if (!resetResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", resetResult.Errors.Select(error => error.Description));
            return Back(staff.Id);
        }

        staff.MustChangePassword = true;
        var updateResult = await _userManager.UpdateAsync(staff);
        if (!updateResult.Succeeded)
        {
            TempData["ErrorMessage"] =
                "Mật khẩu đã được cấp lại nhưng không thể đánh dấu yêu cầu đổi mật khẩu. Vui lòng thử lại ngay.";
            return Back(staff.Id);
        }

        // Làm mất hiệu lực phiên đăng nhập cũ; middleware sẽ buộc Staff đổi mật khẩu trước khi vào nghiệp vụ.
        await _userManager.UpdateSecurityStampAsync(staff);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "ResetStaffTemporaryPassword",
            nameof(ApplicationUser),
            staff.Id,
            $"Quản trị viên cấp lại mật khẩu tạm cho nhân viên {staff.EmployeeCode ?? staff.Email}; nhân viên bắt buộc đổi mật khẩu ở lần đăng nhập tiếp theo.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã cấp lại mật khẩu tạm. Nhân viên phải đổi mật khẩu ngay ở lần đăng nhập tiếp theo; Quản trị viên không thể xem mật khẩu sau khi nhân viên đổi.";

        return Back(staff.Id);
    }

    private IActionResult Back(string id) =>
        RedirectToAction("Details", "AdminStaff", new { id });
}
