using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Accounts;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

public class AccountController : Controller
{
    private readonly IAccountService _accountService;

    public AccountController(IAccountService accountService)
    {
        _accountService = accountService;
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
        if (!model.AcceptTerms)
        {
            ModelState.AddModelError(
                nameof(model.AcceptTerms),
                "Bạn cần đồng ý với điều khoản sử dụng và chính sách bảo mật.");
        }

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
                ModelState.AddModelError(string.Empty, TranslateIdentityError(error));
            }

            return View(model);
        }

        // Đảm bảo tài khoản vừa tạo không được giữ trạng thái đăng nhập tự động.
        await _accountService.LogoutAsync();

        TempData["LoginSuccessMessage"] =
            "Đăng ký tài khoản thành công. Vui lòng đăng nhập để tiếp tục.";

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
                TranslateLoginError(result.ErrorMessage));
            return View(model);
        }

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
        {
            return LocalRedirect(model.ReturnUrl);
        }

        return result.IsManager
            ? RedirectToAction("Index", "Dashboard")
            : RedirectToAction("Index", "Home");
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

    private static string TranslateIdentityError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Không thể tạo tài khoản. Vui lòng kiểm tra lại thông tin.";
        }

        if (error.Contains("at least one lowercase", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu phải có ít nhất một chữ thường (a–z).";
        }

        if (error.Contains("at least one uppercase", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu phải có ít nhất một chữ hoa (A–Z).";
        }

        if (error.Contains("at least one digit", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu phải có ít nhất một chữ số (0–9).";
        }

        if (error.Contains("non alphanumeric", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu phải có ít nhất một ký tự đặc biệt, ví dụ: !, @, # hoặc $.";
        }

        if (error.Contains("must be at least", StringComparison.OrdinalIgnoreCase))
        {
            var number = Regex.Match(error, @"\d+").Value;
            return string.IsNullOrWhiteSpace(number)
                ? "Mật khẩu chưa đủ độ dài tối thiểu."
                : $"Mật khẩu phải có ít nhất {number} ký tự.";
        }

        if (error.Contains("different characters", StringComparison.OrdinalIgnoreCase))
        {
            var number = Regex.Match(error, @"\d+").Value;
            return string.IsNullOrWhiteSpace(number)
                ? "Mật khẩu phải sử dụng nhiều ký tự khác nhau hơn."
                : $"Mật khẩu phải có ít nhất {number} ký tự khác nhau.";
        }

        if (error.Contains("email", StringComparison.OrdinalIgnoreCase)
            && error.Contains("already taken", StringComparison.OrdinalIgnoreCase))
        {
            return "Email này đã được sử dụng. Vui lòng đăng nhập hoặc chọn email khác.";
        }

        if (error.Contains("username", StringComparison.OrdinalIgnoreCase)
            && error.Contains("already taken", StringComparison.OrdinalIgnoreCase))
        {
            return "Tên đăng nhập này đã được sử dụng.";
        }

        if (error.Contains("email", StringComparison.OrdinalIgnoreCase)
            && error.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Địa chỉ email không hợp lệ.";
        }

        if (error.Contains("username", StringComparison.OrdinalIgnoreCase)
            && error.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Tên đăng nhập không hợp lệ.";
        }

        if (error.Contains("phone", StringComparison.OrdinalIgnoreCase)
            && error.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            return "Số điện thoại này đã được sử dụng.";
        }

        return "Không thể tạo tài khoản. Vui lòng kiểm tra lại thông tin và thử lại.";
    }

    private static string TranslateLoginError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Không thể đăng nhập. Vui lòng thử lại.";
        }

        if (error.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || error.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Email hoặc mật khẩu không chính xác.";
        }

        if (error.Contains("locked", StringComparison.OrdinalIgnoreCase))
        {
            return "Tài khoản đang tạm khóa. Vui lòng thử lại sau hoặc liên hệ SmartCar.";
        }

        if (error.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return "Tài khoản chưa được phép đăng nhập. Vui lòng liên hệ SmartCar.";
        }

        return error;
    }
}
