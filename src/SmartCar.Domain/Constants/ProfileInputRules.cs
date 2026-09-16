using System.ComponentModel.DataAnnotations;

namespace SmartCar.Domain.Constants;

public static class ProfileInputRules
{
    public const int FullNameMaxLength = 100;
    public const int AddressMaxLength = 250;
    public const string PhonePattern = @"^(0|\+84)[0-9]{9}$";
    public const string FullNameError = "Họ và tên chỉ được chứa chữ cái và khoảng trắng.";
    public const string PhoneError = "Số điện thoại phải gồm 10 chữ số bắt đầu bằng 0, hoặc dùng mã quốc gia +84.";

    public static string NormalizeFullName(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string NormalizePhoneNumber(string? value)
    {
        var phone = (value ?? string.Empty).Trim()
            .Replace(" ", string.Empty).Replace(".", string.Empty).Replace("-", string.Empty);
        return phone.StartsWith("+84", StringComparison.Ordinal) ? $"0{phone[3..]}" : phone;
    }

    public static bool IsValidPhoneNumber(string value) =>
        value.Length == 10 && value[0] == '0' && value.All(character => character >= '0' && character <= '9');
}

// Server-side validation: .NET Unicode regex syntax is not portable to jQuery's RegExp.
[AttributeUsage(AttributeTargets.Property)]
public sealed class ValidFullNameAttribute : ValidationAttribute
{
    public ValidFullNameAttribute() : base(ProfileInputRules.FullNameError) { }

    public override bool IsValid(object? value) => value is null ||
        (value is string name && name.All(character => char.IsLetter(character) || character == ' '));
}
