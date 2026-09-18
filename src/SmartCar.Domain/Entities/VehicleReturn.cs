using System.ComponentModel.DataAnnotations;

namespace SmartCar.Domain.Entities;

public class VehicleReturn
{
    private const string LegacyAccessoryLabel = "Phụ kiện khi trả:";
    private const string LegacyNoteSeparator = " | Ghi chú: ";

    private string? _accessoryStatus;
    private string? _notes;

    public int VehicleReturnId { get; set; }
    public int BookingId { get; set; }
    public DateTime ReturnedAt { get; set; }
    public int Mileage { get; set; }
    public string FuelLevel { get; set; } = string.Empty;
    public string? ExteriorCondition { get; set; }
    public string? InteriorCondition { get; set; }

    [MaxLength(1000)]
    public string? AccessoryStatus
    {
        get => !string.IsNullOrWhiteSpace(_accessoryStatus)
            ? _accessoryStatus
            : ParseLegacyAccessoryStatus(_notes);
        set => _accessoryStatus = Normalize(value);
    }

    public bool HasDamage { get; set; }
    public bool IsLateReturn { get; set; }
    public int LateMinutes { get; set; }
    public decimal LateFee { get; set; }
    public string? ImagePaths { get; set; }

    public string? Notes
    {
        get => _notes;
        set
        {
            _notes = value;

            // Dữ liệu cũ từng nhét tình trạng phụ kiện vào Notes. Khi code cũ vẫn tạo
            // chuỗi đó, đồng thời chụp giá trị vào cột AccessoryStatus mới để từ đây
            // phụ kiện có dữ liệu riêng và không còn phụ thuộc InteriorCondition.
            if (string.IsNullOrWhiteSpace(_accessoryStatus))
            {
                _accessoryStatus = ParseLegacyAccessoryStatus(value);
            }
        }
    }

    public bool CustomerIdentityVerified { get; set; }
    public string? IdentityVerifiedByStaffId { get; set; }
    public DateTime? IdentityVerifiedAt { get; set; }
    public string? ReturnerFaceImagePath { get; set; }
    public DateTime? ReturnerFaceCapturedAt { get; set; }
    public string? ReturnerFaceCaptureMethod { get; set; }

    public bool SignedDocumentVerified { get; set; }
    public string? SignedDocumentVerifiedByStaffId { get; set; }
    public DateTime? SignedDocumentVerifiedAt { get; set; }

    public Booking Booking { get; set; } = null!;
    public ICollection<AdditionalCharge> AdditionalCharges { get; set; } = new List<AdditionalCharge>();

    private static string? ParseLegacyAccessoryStatus(string? notes)
    {
        var raw = Normalize(notes);
        if (raw is null || !raw.StartsWith(LegacyAccessoryLabel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var body = raw[LegacyAccessoryLabel.Length..].Trim();
        var separatorIndex = body.IndexOf(LegacyNoteSeparator, StringComparison.Ordinal);
        var accessoryStatus = separatorIndex >= 0
            ? body[..separatorIndex].Trim()
            : body;

        return Normalize(accessoryStatus);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}