using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Services;

public sealed record KycPackageValidationError(string Key, string Message);

/// <summary>
/// Cross-field/file validation dùng chung cho toàn bộ gói KYC Customer.
/// DataAnnotation của từng trường nằm trong KycCitizenIdInputViewModel và
/// KycDrivingLicenseInputViewModel; class này chỉ xử lý các rule cần so sánh
/// nhiều trường hoặc đọc nội dung file.
/// </summary>
public static class KycPackageValidator
{
    public const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    public static async Task<IReadOnlyList<KycPackageValidationError>> ValidateAsync(
        KycPackageSubmitViewModel model,
        DateTime? requiredValidThrough,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var errors = new List<KycPackageValidationError>();
        var citizen = model.CitizenIdVerification;
        var license = model.DrivingLicenseVerification;

        await ValidateImageAsync(
            errors,
            "CitizenIdVerification.FrontImage",
            citizen.FrontImage,
            cancellationToken);
        await ValidateImageAsync(
            errors,
            "CitizenIdVerification.BackImage",
            citizen.BackImage,
            cancellationToken);
        await ValidateImageAsync(
            errors,
            "DrivingLicenseVerification.FrontImage",
            license.FrontImage,
            cancellationToken);
        await ValidateImageAsync(
            errors,
            "DrivingLicenseVerification.BackImage",
            license.BackImage,
            cancellationToken);

        if (await ImageFileValidator.HaveSameContentAsync(
                citizen.FrontImage,
                citizen.BackImage,
                cancellationToken))
        {
            errors.Add(new KycPackageValidationError(
                "CitizenIdVerification.BackImage",
                "Ảnh CCCD mặt trước và mặt sau phải là hai ảnh khác nhau."));
        }

        if (await ImageFileValidator.HaveSameContentAsync(
                license.FrontImage,
                license.BackImage,
                cancellationToken))
        {
            errors.Add(new KycPackageValidationError(
                "DrivingLicenseVerification.BackImage",
                "Ảnh GPLX mặt trước và mặt sau phải là hai ảnh khác nhau."));
        }

        if (citizen.DateOfBirth.HasValue &&
            citizen.DateOfBirth.Value.Date > DateTime.Today.AddYears(-18))
        {
            errors.Add(new KycPackageValidationError(
                "CitizenIdVerification.DateOfBirth",
                "Khách thuê xe phải đủ 18 tuổi."));
        }

        AddExpiryErrors(
            errors,
            "CCCD",
            "CitizenIdVerification.ExpiryDate",
            citizen.ExpiryDate,
            requiredValidThrough);

        AddExpiryErrors(
            errors,
            "GPLX",
            "DrivingLicenseVerification.ExpiryDate",
            license.ExpiryDate,
            requiredValidThrough);

        if (!model.ConfirmSamePerson)
        {
            errors.Add(new KycPackageValidationError(
                nameof(KycPackageSubmitViewModel.ConfirmSamePerson),
                "Bạn cần xác nhận CCCD và GPLX thuộc cùng một người trước khi gửi hồ sơ."));
        }

        return errors;
    }

    private static async Task ValidateImageAsync(
        ICollection<KycPackageValidationError> errors,
        string key,
        Microsoft.AspNetCore.Http.IFormFile? file,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(
            file,
            MaximumDocumentImageBytes,
            cancellationToken);

        if (error is not null)
        {
            errors.Add(new KycPackageValidationError(key, error));
        }
    }

    private static void AddExpiryErrors(
        ICollection<KycPackageValidationError> errors,
        string documentName,
        string key,
        DateTime? expiryDate,
        DateTime? requiredValidThrough)
    {
        if (expiryDate.HasValue && expiryDate.Value.Date < DateTime.Today)
        {
            errors.Add(new KycPackageValidationError(
                key,
                $"{documentName} đã hết hạn."));
        }

        if (requiredValidThrough.HasValue &&
            expiryDate.HasValue &&
            expiryDate.Value.Date < requiredValidThrough.Value.Date)
        {
            errors.Add(new KycPackageValidationError(
                key,
                $"{documentName} phải còn hiệu lực ít nhất đến ngày trả xe {requiredValidThrough.Value:dd/MM/yyyy}."));
        }
    }
}
