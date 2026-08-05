using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class AdminDashboardViewModel
{
    public int TotalVehicles { get; init; }
    public int AvailableVehicles { get; init; }
    public int RentedVehicles { get; init; }
    public int MaintenanceVehicles { get; init; }
    public int PendingBookings { get; init; }
    public decimal MonthlyRevenue { get; init; }
    public int PendingDocuments { get; init; }
    public int TodayPickups { get; init; }
    public int TodayReturns { get; init; }
    public int DamagedReturns { get; init; }
    public IReadOnlyList<AdminBookingRowViewModel> RecentBookings { get; init; } = Array.Empty<AdminBookingRowViewModel>();
    public IReadOnlyList<AdminOperationRowViewModel> TodayOperations { get; init; } = Array.Empty<AdminOperationRowViewModel>();
    public IReadOnlyList<AdminAlertViewModel> Alerts { get; init; } = Array.Empty<AdminAlertViewModel>();
    public IReadOnlyList<AdminRevenuePointViewModel> RevenuePoints { get; init; } = Array.Empty<AdminRevenuePointViewModel>();
}

public sealed class AdminBookingListViewModel
{
    public string? Search { get; init; }
    public BookingStatus? Status { get; init; }
    public IReadOnlyList<AdminBookingRowViewModel> Bookings { get; init; } = Array.Empty<AdminBookingRowViewModel>();
    public IReadOnlyDictionary<BookingStatus, int> StatusCounts { get; init; } = new Dictionary<BookingStatus, int>();
}

public sealed class AdminBookingRowViewModel
{
    public int BookingId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerEmail { get; init; } = string.Empty;
    public string CustomerPhone { get; init; } = string.Empty;
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public DateTime PickupDate { get; init; }
    public DateTime ReturnDate { get; init; }
    public decimal TotalAmount { get; init; }
    public BookingStatus Status { get; init; }
    public PaymentStatus? PaymentStatus { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsUrgent { get; init; }
}

public sealed class AdminBookingDetailsViewModel
{
    public AdminBookingRowViewModel Booking { get; init; } = new();
    public decimal DailyPrice { get; init; }
    public int NumberOfDays { get; init; }
    public decimal RentalAmount { get; init; }
    public decimal AdditionalAmount { get; init; }
    public string? CancelReason { get; init; }
    public decimal? PaymentAmount { get; init; }
    public string? PaymentMethod { get; init; }
    public string? TransactionCode { get; init; }
    public DateTime? PaidAt { get; init; }
    public int VerifiedDocuments { get; init; }
    public int PendingDocuments { get; init; }
    public int RejectedDocuments { get; init; }
    public bool HasHandover { get; init; }
    public bool HasReturn { get; init; }
    public bool HasDamage { get; init; }
    public int? HandoverMileage { get; init; }
    public int? ReturnMileage { get; init; }
    public string? HandoverFuelLevel { get; init; }
    public string? ReturnFuelLevel { get; init; }
}

public sealed class AdminOperationRowViewModel
{
    public int BookingId { get; init; }
    public DateTime Time { get; init; }
    public string OperationType { get; init; } = string.Empty;
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerPhone { get; init; } = string.Empty;
    public BookingStatus Status { get; init; }
}

public sealed class AdminAlertViewModel
{
    public string Severity { get; init; } = "info";
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ActionUrl { get; init; } = string.Empty;
}

public sealed class AdminRevenuePointViewModel
{
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
    public int Percentage { get; init; }
}

public sealed class AdminVehicleListViewModel
{
    public string? Search { get; init; }
    public VehicleStatus? Status { get; init; }
    public IReadOnlyList<AdminVehicleRowViewModel> Vehicles { get; init; } = Array.Empty<AdminVehicleRowViewModel>();
}

public sealed class AdminVehicleRowViewModel
{
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string BrandName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public int ManufactureYear { get; init; }
    public int Seats { get; init; }
    public string Transmission { get; init; } = string.Empty;
    public string FuelType { get; init; } = string.Empty;
    public decimal DailyPrice { get; init; }
    public int CurrentMileage { get; init; }
    public VehicleStatus Status { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public int ActiveBookings { get; init; }
    public int CompletedBookings { get; init; }
    public decimal MonthlyRevenue { get; init; }
    public DateTime? NextBookingDate { get; init; }
    public DateTime? LastMaintenanceDate { get; init; }
}

public sealed class AdminCalendarViewModel
{
    public DateTime Month { get; init; }
    public IReadOnlyList<DateTime> Days { get; init; } = Array.Empty<DateTime>();
    public IReadOnlyList<AdminCalendarVehicleRowViewModel> Vehicles { get; init; } = Array.Empty<AdminCalendarVehicleRowViewModel>();
}

public sealed class AdminCalendarVehicleRowViewModel
{
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public VehicleStatus VehicleStatus { get; init; }
    public IReadOnlyDictionary<int, AdminCalendarCellViewModel> Cells { get; init; } = new Dictionary<int, AdminCalendarCellViewModel>();
}

public sealed class AdminCalendarCellViewModel
{
    public string Status { get; init; } = "available";
    public string Label { get; init; } = "Trống";
    public int? BookingId { get; init; }
}

public sealed class AdminCustomerListViewModel
{
    public string? Search { get; init; }
    public IReadOnlyList<AdminCustomerRowViewModel> Customers { get; init; } = Array.Empty<AdminCustomerRowViewModel>();
}

public sealed class AdminCustomerRowViewModel
{
    public string CustomerId { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; init; }
    public int TotalBookings { get; init; }
    public int CompletedBookings { get; init; }
    public int VerifiedDocuments { get; init; }
    public int PendingDocuments { get; init; }
}

public sealed class AdminDocumentListViewModel
{
    public DocumentStatus? Status { get; init; }
    public IReadOnlyList<AdminDocumentRowViewModel> Documents { get; init; } = Array.Empty<AdminDocumentRowViewModel>();
}

public sealed class AdminDocumentRowViewModel
{
    public int CustomerDocumentId { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerEmail { get; init; } = string.Empty;
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentNumber { get; init; } = string.Empty;
    public DateTime? ExpiryDate { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public DocumentStatus Status { get; init; }
    public string? RejectionReason { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class AdminPaymentListViewModel
{
    public PaymentStatus? Status { get; init; }
    public IReadOnlyList<AdminPaymentRowViewModel> Payments { get; init; } = Array.Empty<AdminPaymentRowViewModel>();
    public decimal TotalPaid { get; init; }
    public decimal TotalPending { get; init; }
    public decimal TotalRefunded { get; init; }
}

public sealed class AdminPaymentRowViewModel
{
    public int PaymentId { get; init; }
    public int BookingId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string VehicleName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Method { get; init; } = string.Empty;
    public PaymentStatus Status { get; init; }
    public DateTime? PaidAt { get; init; }
    public string? TransactionCode { get; init; }
}

public sealed class AdminMaintenanceListViewModel
{
    public IReadOnlyList<AdminMaintenanceRowViewModel> Records { get; init; } = Array.Empty<AdminMaintenanceRowViewModel>();
    public IReadOnlyList<AdminVehicleOptionViewModel> VehicleOptions { get; init; } = Array.Empty<AdminVehicleOptionViewModel>();
}

public sealed class AdminMaintenanceRowViewModel
{
    public int MaintenanceRecordId { get; init; }
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime? CompletedDate { get; init; }
    public string Content { get; init; } = string.Empty;
    public decimal Cost { get; init; }
    public string? ServiceProvider { get; init; }
    public int Mileage { get; init; }
    public MaintenanceStatus Status { get; init; }
}

public sealed class AdminVehicleOptionViewModel
{
    public int VehicleId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}
