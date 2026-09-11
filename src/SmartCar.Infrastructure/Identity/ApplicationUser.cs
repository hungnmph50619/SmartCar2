using Microsoft.AspNetCore.Identity;

namespace SmartCar.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? AvatarPath { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Các trường dưới đây chỉ được sử dụng cho tài khoản Staff/Nhân viên.
    // Customer/Admin có thể để null để không làm thay đổi luồng tài khoản hiện có.
    public string? EmployeeCode { get; set; }
    public string? CitizenIdNumber { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAt { get; set; }

    // Staff mới do Admin tạo sẽ phải đổi mật khẩu tạm ở lần đăng nhập đầu tiên.
    public bool MustChangePassword { get; set; }
}
