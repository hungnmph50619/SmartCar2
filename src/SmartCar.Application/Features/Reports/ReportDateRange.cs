using System.Globalization;

namespace SmartCar.Application.Features.Reports;

public sealed record ReportDateRange(DateTime From, DateTime To)
{
    // Midnight in Vietnam must remain representable after conversion to UTC.
    public static readonly DateTime MinimumDate = new(1, 1, 2);

    public static bool TryCreate(string? fromDate, string? toDate, DateTime vietnamToday,
        out ReportDateRange? range, out string? error)
    {
        range = null;
        error = null;
        if (fromDate is null && toDate is null)
        {
            range = new(vietnamToday.Date.AddDays(-29), vietnamToday.Date);
            return true;
        }

        if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
            error = "Vui lòng nhập đầy đủ Từ ngày và Đến ngày.";
        else if (!DateTime.TryParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var from) ||
                 !DateTime.TryParseExact(toDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var to))
            error = "Ngày không hợp lệ. Vui lòng chọn lại Từ ngày và Đến ngày.";
        else if (from < MinimumDate || to < MinimumDate)
            error = "Ngày đã chọn nằm ngoài phạm vi báo cáo hỗ trợ.";
        else if (from > vietnamToday.Date || to > vietnamToday.Date)
            error = "Không được chọn ngày trong tương lai (theo giờ Việt Nam).";
        else if (from > to)
            error = "Từ ngày không được sau Đến ngày.";
        else
            range = new(from, to);

        return error is null;
    }
}
