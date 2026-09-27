using SmartCar.Application.Features.Incidents;
using SmartCar.Domain.Enums;

namespace SmartCar.Infrastructure.Services;

internal static class IncidentValidationRules
{
    private const int DescriptionMaxLength = 1500;
    private const int LocationMaxLength = 250;
    private const int EvidencePathsMaxLength = 2000;
    private const int NotesMaxLength = 1000;

    public static string? ValidateCreate(CreateIncidentRequest request)
    {
        if (request.VehicleId <= 0)
        {
            return "Xe không hợp lệ.";
        }

        if (request.BookingId.HasValue && request.BookingId.Value <= 0)
        {
            return "Mã đơn liên quan không hợp lệ.";
        }

        if (!Enum.IsDefined(typeof(IncidentType), request.IncidentType))
        {
            return "Loại sự cố/vi phạm không hợp lệ.";
        }

        if (request.OccurredAt == default)
        {
            return "Vui lòng nhập thời điểm xảy ra.";
        }

        if (request.OccurredAt > DateTime.Now.AddMinutes(5))
        {
            return "Thời điểm xảy ra không được ở tương lai.";
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return "Vui lòng nhập mô tả sự cố.";
        }

        if (request.Description.Trim().Length > DescriptionMaxLength)
        {
            return $"Mô tả không được vượt quá {DescriptionMaxLength:N0} ký tự.";
        }

        if (NormalizeLength(request.Location) > LocationMaxLength)
        {
            return $"Địa điểm không được vượt quá {LocationMaxLength:N0} ký tự.";
        }

        if (NormalizeLength(request.EvidencePaths) > EvidencePathsMaxLength)
        {
            return $"Bằng chứng/thông báo không được vượt quá {EvidencePathsMaxLength:N0} ký tự.";
        }

        if (NormalizeLength(request.Notes) > NotesMaxLength)
        {
            return $"Ghi chú không được vượt quá {NotesMaxLength:N0} ký tự.";
        }

        if (request.EstimatedCost < 0 ||
            request.FineAmount < 0 ||
            request.CustomerLiabilityAmount < 0)
        {
            return "Các khoản tiền không được âm.";
        }

        if (!IsWholeVnd(request.EstimatedCost) ||
            !IsWholeVnd(request.FineAmount) ||
            !IsWholeVnd(request.CustomerLiabilityAmount))
        {
            return "Các khoản tiền phải nhập theo số nguyên đồng, không nhập số lẻ.";
        }

        if (!string.IsNullOrWhiteSpace(request.EvidencePaths) &&
            !HasValidEvidenceReferences(request.EvidencePaths))
        {
            return "Đường dẫn bằng chứng không hợp lệ. Hãy nhập đường dẫn ảnh/tài liệu hợp lệ, phân cách bằng dấu ;.";
        }

        return null;
    }

    public static string? ValidateResolve(ResolveIncidentRequest request)
    {
        if (request.IncidentId <= 0)
        {
            return "Mã sự cố không hợp lệ.";
        }

        if (request.ActualCost < 0 ||
            request.FineAmount < 0 ||
            request.CustomerLiabilityAmount < 0)
        {
            return "Các khoản tiền không được âm.";
        }

        if (!IsWholeVnd(request.ActualCost) ||
            !IsWholeVnd(request.FineAmount) ||
            !IsWholeVnd(request.CustomerLiabilityAmount))
        {
            return "Các khoản tiền phải nhập theo số nguyên đồng, không nhập số lẻ.";
        }

        if (NormalizeLength(request.Notes) > NotesMaxLength)
        {
            return $"Ghi chú không được vượt quá {NotesMaxLength:N0} ký tự.";
        }

        return null;
    }

    private static int NormalizeLength(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Trim().Length;

    private static bool IsWholeVnd(decimal value) =>
        value == decimal.Truncate(value);

    private static bool HasValidEvidenceReferences(string value)
    {
        var references = value.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        if (references.Length == 0)
        {
            return false;
        }

        foreach (var reference in references)
        {
            if (reference.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            if (Uri.TryCreate(reference, UriKind.Absolute, out var absoluteUri))
            {
                if (absoluteUri.Scheme is not ("http" or "https"))
                {
                    return false;
                }

                continue;
            }

            var normalized = reference.Replace('\\', '/');
            var looksLikeRelativePath =
                normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.StartsWith("~/", StringComparison.Ordinal) ||
                normalized.Contains('/', StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(Path.GetExtension(normalized));

            if (!looksLikeRelativePath)
            {
                return false;
            }
        }

        return true;
    }
}
