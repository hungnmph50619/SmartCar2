using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Accounts;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

public class AccountController : Controller
{
    private readonly IAccountService _accountService;
    private readonly IEmailService _emailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAccountService accountService,
        IEmailService emailService,
        UserManager<ApplicationUser> userManager,
        IWebHostEnvironment environment,
        ILogger<AccountController> logger)
    {
        _accountService = accountService;
        _emailService = emailService;
        _userManager = userManager;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new RegisterViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(
        RegisterViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _accountService.RegisterCustomerAsync(
            new RegisterCustomerRequest(
                model.FullName,
                model.PhoneNumber,
                model.Email,
                model.Password),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        TempData["SuccessMessage"] = "Đăng ký thành công. Vui lòng đăng nhập để tiếp tục.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(
        LoginViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _accountService.LoginAsync(
            new LoginRequest(model.Email, model.Password, model.RememberMe),
            cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(
                string.Empty,
                result.ErrorMessage ?? "Không thể đăng nhập.");
            return View(model);
        }

        if (result.IsAdmin)
        {
            return RedirectToAction("Index", "Dashboard");
        }

        if (result.IsStaff)
        {
            var staff = await _userManager.FindByEmailAsync(model.Email.Trim());
            if (staff?.MustChangePassword == true)
            {
                return RedirectToAction(nameof(FirstLoginPassword));
            }

            return RedirectToAction("Index", "Staff");
        }

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
        {
            return LocalRedirect(model.ReturnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    [Authorize(Roles = RoleNames.Staff)]
    public async Task<IActionResult> FirstLoginPassword()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _accountService.LogoutAsync();
            return RedirectToAction(nameof(Login));
        }

        if (!user.MustChangePassword)
        {
            return RedirectToAction("Index", "Staff");
        }

        return View(new ChangePasswordViewModel());
    }

    [HttpPost]
    [Authorize(Roles = RoleNames.Staff)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FirstLoginPassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _accountService.LogoutAsync();
            return RedirectToAction(nameof(Login));
        }

        if (!user.MustChangePassword)
        {
            return RedirectToAction("Index", "Staff");
        }

        var result = await _userManager.ChangePasswordAsync(
            user,
            model.CurrentPassword,
            model.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        user.MustChangePassword = false;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Đã đổi mật khẩu nhưng không thể cập nhật trạng thái tài khoản. Vui lòng thử lại.");
            return View(model);
        }

        await _accountService.LogoutAsync();
        TempData["SuccessMessage"] = "Đổi mật khẩu thành công. Vui lòng đăng nhập lại bằng mật khẩu mới để vào hệ thống Nhân viên.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPassword()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new ForgotPasswordViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(
        ForgotPasswordViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var email = model.Email.Trim();
        var token = await _accountService.GeneratePasswordResetTokenAsync(
            email,
            cancellationToken);

        // Luôn trả cùng một trang xác nhận để tránh tiết lộ email nào đang tồn tại.
        if (token is not null)
        {
            var resetUrl = Url.Action(
                nameof(ResetPassword),
                "Account",
                new { email, token },
                Request.Scheme);

            if (string.IsNullOrWhiteSpace(resetUrl))
            {
                _logger.LogError(
                    "Không thể tạo URL đặt lại mật khẩu cho tài khoản {Email}.",
                    email);

                if (_environment.IsDevelopment())
                {
                    ModelState.AddModelError(
                        string.Empty,
                        "Không thể tạo liên kết đặt lại mật khẩu. Vui lòng thử lại.");
                    return View(model);
                }
            }
            else
            {
                try
                {
                    await _emailService.SendPasswordResetEmailAsync(
                        email,
                        resetUrl,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Không thể gửi email đặt lại mật khẩu đến {Email}.",
                        email);

                    if (_environment.IsDevelopment())
                    {
                        ModelState.AddModelError(
                            string.Empty,
                            "Không gửi được email khôi phục. Hãy kiểm tra cấu hình Email:Smtp và mật khẩu ứng dụng của email gửi.");
                        return View(model);
                    }
                }
            }
        }

        return View("ForgotPasswordConfirmation");
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ResetPassword(string? email, string? token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            TempData["ErrorMessage"] = "Liên kết đặt lại mật khẩu không hợp lệ.";
            return RedirectToAction(nameof(Login));
        }

        return View(new ResetPasswordViewModel
        {
            Email = email,
            Token = token
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(
        ResetPasswordViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _accountService.ResetPasswordAsync(
            model.Email,
            model.Token,
            model.Password,
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        TempData["SuccessMessage"] = "Đặt lại mật khẩu thành công. Vui lòng đăng nhập bằng mật khẩu mới.";
        return RedirectToAction(nameof(Login));
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _accountService.LogoutAsync();
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();
}
