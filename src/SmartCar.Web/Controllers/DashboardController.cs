using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Manager)]
public class DashboardController : Controller
{
    private static readonly BookingStatus[] ActiveBookingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public DashboardController(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var sevenDaysAgo = today.AddDays(-6);

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Vehicle)
            .Include(booking => booking.Payment)
            .Include(booking => booking.VehicleReturn)
            .OrderByDescending(booking => booking.CreatedAt)
            .ToListAsync(cancellationToken);

        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var userLookup = await LoadUsersAsync(
            bookings.Select(booking => booking.CustomerId),
            cancellationToken);

        var paidPayments = bookings
            .Where(booking => booking.Payment?.Status == PaymentStatus.Paid)
            .Select(booking => booking.Payment!)
            .ToList();

        var dailyAmounts = Enumerable.Range(0, 7)
            .Select(offset => sevenDaysAgo.AddDays(offset))
            .Select(date => new
            {
                Date = date,
                Amount = paidPayments
                    .Where(payment => payment.PaidAt?.Date == date.Date)
                    .Sum(payment => payment.Amount)
            })
            .ToList();
        var maxDailyRevenue = Math.Max(1m, dailyAmounts.Max(item => item.Amount));

        var recentBookings = bookings
            .Take(8)
            .Select(booking => MapBookingRow(booking, userLookup))
            .ToList();

        var todayOperations = bookings
            .Where(booking =>
                booking.PickupDate.Date == today
                || booking.ReturnDate.Date == today)
            .SelectMany(booking =>
            {
                var user = GetUser(userLookup, booking.CustomerId);
                var rows = new List<AdminOperationRowViewModel>();
                if (booking.PickupDate.Date == today)
                {
                    rows.Add(new AdminOperationRowViewModel
                    {
                        BookingId = booking.BookingId,
                        Time = booking.PickupDate,
                        OperationType = "Giao xe",
                        VehicleName = booking.Vehicle.VehicleName,
                        LicensePlate = booking.Vehicle.LicensePlate,
                        CustomerName = user.FullName,
                        CustomerPhone = user.PhoneNumber,
                        Status = booking.Status
                    });
                }

                if (booking.ReturnDate.Date == today)
                {
                    rows.Add(new AdminOperationRowViewModel
                    {
                        BookingId = booking.BookingId,
                        Time = booking.ReturnDate,
                        OperationType = "Nhận xe",
                        VehicleName = booking.Vehicle.VehicleName,
                        LicensePlate = booking.Vehicle.LicensePlate,
                        CustomerName = user.FullName,
                        CustomerPhone = user.PhoneNumber,
                        Status = booking.Status
                    });
                }

                return rows;
            })
            .OrderBy(operation => operation.Time)
            .ToList();

        var pendingConfirmation = bookings.Count(booking => booking.Status == BookingStatus.PendingConfirmation);
        var unpaidBookings = bookings.Count(booking => booking.Status == BookingStatus.PendingPayment);
        var pendingDocuments = documents.Count(document => document.Status == DocumentStatus.Pending);
        var damagedReturns = bookings.Count(booking => booking.VehicleReturn?.HasDamage == true);

        var alerts = new List<AdminAlertViewModel>();
        if (pendingConfirmation > 0)
        {
            alerts.Add(new AdminAlertViewModel
            {
                Severity = "danger",
                Title = $"{pendingConfirmation} đơn đang chờ xác nhận",
                Description = "Kiểm tra lịch xe và phản hồi khách trong thời gian quy định.",
                ActionUrl = Url.Action(nameof(Bookings), new { status = BookingStatus.PendingConfirmation }) ?? "/Dashboard/Bookings"
            });
        }

        if (unpaidBookings > 0)
        {
            alerts.Add(new AdminAlertViewModel
            {
                Severity = "warning",
                Title = $"{unpaidBookings} đơn đang chờ thanh toán cọc",
                Description = "Theo dõi thời hạn giữ chỗ và xử lý các đơn quá hạn.",
                ActionUrl = Url.Action(nameof(Bookings), new { status = BookingStatus.PendingPayment }) ?? "/Dashboard/Bookings"
            });
        }

        if (pendingDocuments > 0)
        {
            alerts.Add(new AdminAlertViewModel
            {
                Severity = "info",
                Title = $"{pendingDocuments} giấy tờ cần xác minh",
                Description = "Ưu tiên hồ sơ của khách sắp đến ngày nhận xe.",
                ActionUrl = Url.Action(nameof(Documents), new { status = DocumentStatus.Pending }) ?? "/Dashboard/Documents"
            });
        }

        if (damagedReturns > 0)
        {
            alerts.Add(new AdminAlertViewModel
            {
                Severity = "danger",
                Title = $"{damagedReturns} lượt trả xe có ghi nhận hư hỏng",
                Description = "Kiểm tra biên bản trả xe và các khoản phụ phí liên quan.",
                ActionUrl = Url.Action(nameof(Bookings), new { status = BookingStatus.PendingInspection }) ?? "/Dashboard/Bookings"
            });
        }

        if (alerts.Count == 0)
        {
            alerts.Add(new AdminAlertViewModel
            {
                Severity = "success",
                Title = "Không có công việc khẩn cấp",
                Description = "Các đầu việc vận hành đang ở trạng thái ổn định.",
                ActionUrl = Url.Action(nameof(Operations)) ?? "/Dashboard/Operations"
            });
        }

        var model = new AdminDashboardViewModel
        {
            TotalVehicles = vehicles.Count,
            AvailableVehicles = vehicles.Count(vehicle => vehicle.Status == VehicleStatus.Available),
            RentedVehicles = vehicles.Count(vehicle => vehicle.Status == VehicleStatus.Rented),
            MaintenanceVehicles = vehicles.Count(vehicle => vehicle.Status == VehicleStatus.Maintenance),
            PendingBookings = pendingConfirmation,
            MonthlyRevenue = paidPayments
                .Where(payment => payment.PaidAt >= monthStart && payment.PaidAt < monthEnd)
                .Sum(payment => payment.Amount),
            PendingDocuments = pendingDocuments,
            TodayPickups = bookings.Count(booking => booking.PickupDate.Date == today),
            TodayReturns = bookings.Count(booking => booking.ReturnDate.Date == today),
            DamagedReturns = damagedReturns,
            RecentBookings = recentBookings,
            TodayOperations = todayOperations,
            Alerts = alerts,
            RevenuePoints = dailyAmounts.Select(item => new AdminRevenuePointViewModel
            {
                Date = item.Date,
                Amount = item.Amount,
                Percentage = item.Amount == 0 ? 4 : (int)Math.Max(4, Math.Round(item.Amount / maxDailyRevenue * 100))
            }).ToList()
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Bookings(
        BookingStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Vehicle)
            .Include(booking => booking.Payment)
            .OrderByDescending(booking => booking.CreatedAt)
            .ToListAsync(cancellationToken);

        var userLookup = await LoadUsersAsync(
            bookings.Select(booking => booking.CustomerId),
            cancellationToken);

        var rows = bookings
            .Select(booking => MapBookingRow(booking, userLookup))
            .ToList();

        if (status.HasValue)
        {
            rows = rows.Where(row => row.Status == status.Value).ToList();
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();
            rows = rows.Where(row =>
                row.BookingId.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.CustomerEmail.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return View(new AdminBookingListViewModel
        {
            Status = status,
            Search = search,
            Bookings = rows,
            StatusCounts = bookings
                .GroupBy(booking => booking.Status)
                .ToDictionary(group => group.Key, group => group.Count())
        });
    }

    [HttpGet]
    public async Task<IActionResult> BookingDetails(
        int id,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .Include(item => item.Payment)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        var userLookup = await LoadUsersAsync(new[] { booking.CustomerId }, cancellationToken);
        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => document.CustomerId == booking.CustomerId)
            .ToListAsync(cancellationToken);

        return View(new AdminBookingDetailsViewModel
        {
            Booking = MapBookingRow(booking, userLookup),
            DailyPrice = booking.DailyPrice,
            NumberOfDays = booking.NumberOfDays,
            RentalAmount = booking.RentalAmount,
            AdditionalAmount = booking.AdditionalAmount,
            CancelReason = booking.CancelReason,
            PaymentAmount = booking.Payment?.Amount,
            PaymentMethod = booking.Payment?.Method,
            TransactionCode = booking.Payment?.TransactionCode,
            PaidAt = booking.Payment?.PaidAt,
            VerifiedDocuments = documents.Count(document => document.Status == DocumentStatus.Verified),
            PendingDocuments = documents.Count(document => document.Status == DocumentStatus.Pending),
            RejectedDocuments = documents.Count(document => document.Status == DocumentStatus.Rejected),
            HasHandover = booking.Handover is not null,
            HasReturn = booking.VehicleReturn is not null,
            HasDamage = booking.VehicleReturn?.HasDamage == true,
            HandoverMileage = booking.Handover?.Mileage,
            ReturnMileage = booking.VehicleReturn?.Mileage,
            HandoverFuelLevel = booking.Handover?.FuelLevel,
            ReturnFuelLevel = booking.VehicleReturn?.FuelLevel
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeBookingStatus(
        int id,
        BookingStatus newStatus,
        string? reason,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payment)
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (!CanTransition(booking.Status, newStatus))
        {
            TempData["AdminError"] = "Không thể chuyển đơn sang trạng thái đã chọn từ trạng thái hiện tại.";
            return RedirectToAction(nameof(BookingDetails), new { id });
        }

        if (newStatus is BookingStatus.Rejected or BookingStatus.Cancelled
            && string.IsNullOrWhiteSpace(reason))
        {
            TempData["AdminError"] = "Vui lòng nhập lý do từ chối hoặc hủy đơn.";
            return RedirectToAction(nameof(BookingDetails), new { id });
        }

        booking.Status = newStatus;
        if (newStatus is BookingStatus.Rejected or BookingStatus.Cancelled)
        {
            booking.CancelReason = reason!.Trim();
        }

        if (newStatus == BookingStatus.PendingPayment && booking.Payment is null)
        {
            booking.Payment = new Payment
            {
                BookingId = booking.BookingId,
                Amount = Math.Min(500_000m, booking.TotalAmount),
                Method = "Mô phỏng",
                Status = PaymentStatus.Pending
            };
        }

        if (newStatus == BookingStatus.Paid)
        {
            booking.Payment ??= new Payment
            {
                BookingId = booking.BookingId,
                Amount = Math.Min(500_000m, booking.TotalAmount),
                Method = "Mô phỏng"
            };
            booking.Payment.Status = PaymentStatus.Paid;
            booking.Payment.PaidAt ??= DateTime.UtcNow;
            booking.Payment.TransactionCode ??= $"SC{DateTime.UtcNow:yyyyMMddHHmmss}{booking.BookingId}";
        }

        if (newStatus == BookingStatus.Rented)
        {
            booking.Vehicle.Status = VehicleStatus.Rented;
        }
        else if (newStatus is BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.Rejected)
        {
            if (booking.Vehicle.Status == VehicleStatus.Rented)
            {
                booking.Vehicle.Status = VehicleStatus.Available;
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = GetBookingNotificationTitle(newStatus),
            Message = GetBookingNotificationMessage(booking, newStatus, reason),
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã cập nhật trạng thái đơn thuê và gửi thông báo cho khách hàng.";
        return RedirectToAction(nameof(BookingDetails), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Vehicles(
        VehicleStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .Include(vehicle => vehicle.Brand)
            .Include(vehicle => vehicle.Images)
            .Include(vehicle => vehicle.Bookings)
                .ThenInclude(booking => booking.Payment)
            .Include(vehicle => vehicle.MaintenanceRecords)
            .OrderBy(vehicle => vehicle.VehicleName)
            .ToListAsync(cancellationToken);

        if (status.HasValue)
        {
            vehicles = vehicles.Where(vehicle => vehicle.Status == status.Value).ToList();
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();
            vehicles = vehicles.Where(vehicle =>
                vehicle.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || vehicle.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || vehicle.Brand.BrandName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var rows = vehicles.Select(vehicle => new AdminVehicleRowViewModel
        {
            VehicleId = vehicle.VehicleId,
            VehicleName = vehicle.VehicleName,
            BrandName = vehicle.Brand.BrandName,
            LicensePlate = vehicle.LicensePlate,
            ManufactureYear = vehicle.ManufactureYear,
            Seats = vehicle.Seats,
            Transmission = vehicle.Transmission,
            FuelType = vehicle.FuelType,
            DailyPrice = vehicle.DailyPrice,
            CurrentMileage = vehicle.CurrentMileage,
            Status = vehicle.Status,
            ImageUrl = NormalizeImagePath(vehicle.Images
                .OrderByDescending(image => image.IsPrimary)
                .ThenBy(image => image.SortOrder)
                .FirstOrDefault()?.ImagePath),
            ActiveBookings = vehicle.Bookings.Count(booking => ActiveBookingStatuses.Contains(booking.Status)),
            CompletedBookings = vehicle.Bookings.Count(booking => booking.Status == BookingStatus.Completed),
            MonthlyRevenue = vehicle.Bookings
                .Where(booking =>
                    booking.Payment?.Status == PaymentStatus.Paid
                    && booking.Payment.PaidAt >= monthStart
                    && booking.Payment.PaidAt < monthEnd)
                .Sum(booking => booking.Payment!.Amount),
            NextBookingDate = vehicle.Bookings
                .Where(booking => ActiveBookingStatuses.Contains(booking.Status) && booking.PickupDate >= DateTime.Now)
                .OrderBy(booking => booking.PickupDate)
                .Select(booking => (DateTime?)booking.PickupDate)
                .FirstOrDefault(),
            LastMaintenanceDate = vehicle.MaintenanceRecords
                .OrderByDescending(record => record.StartDate)
                .Select(record => (DateTime?)record.StartDate)
                .FirstOrDefault()
        }).ToList();

        return View(new AdminVehicleListViewModel
        {
            Status = status,
            Search = search,
            Vehicles = rows
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeVehicleStatus(
        int id,
        VehicleStatus newStatus,
        CancellationToken cancellationToken)
    {
        var vehicle = await _dbContext.Vehicles
            .Include(item => item.Bookings)
            .FirstOrDefaultAsync(item => item.VehicleId == id, cancellationToken);

        if (vehicle is null)
        {
            return NotFound();
        }

        if (newStatus is VehicleStatus.Maintenance or VehicleStatus.Inactive)
        {
            var hasCurrentRental = vehicle.Bookings.Any(booking =>
                booking.Status is BookingStatus.Rented or BookingStatus.PendingInspection);
            if (hasCurrentRental)
            {
                TempData["AdminError"] = "Không thể khóa hoặc bảo trì xe khi xe đang trong chuyến thuê.";
                return RedirectToAction(nameof(Vehicles));
            }
        }

        vehicle.Status = newStatus;
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã cập nhật trạng thái xe.";
        return RedirectToAction(nameof(Vehicles));
    }

    [HttpGet]
    public async Task<IActionResult> Calendar(
        DateTime? month,
        CancellationToken cancellationToken)
    {
        var selectedMonth = new DateTime(
            (month ?? DateTime.Today).Year,
            (month ?? DateTime.Today).Month,
            1);
        var monthEnd = selectedMonth.AddMonths(1);
        var days = Enumerable.Range(0, (monthEnd - selectedMonth).Days)
            .Select(offset => selectedMonth.AddDays(offset))
            .ToList();

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .Include(vehicle => vehicle.Bookings)
            .OrderBy(vehicle => vehicle.VehicleName)
            .ToListAsync(cancellationToken);

        var rows = vehicles.Select(vehicle =>
        {
            var cells = new Dictionary<int, AdminCalendarCellViewModel>();
            foreach (var day in days)
            {
                if (vehicle.Status == VehicleStatus.Maintenance)
                {
                    cells[day.Day] = new AdminCalendarCellViewModel
                    {
                        Status = "maintenance",
                        Label = "Bảo trì"
                    };
                    continue;
                }

                if (vehicle.Status == VehicleStatus.Inactive)
                {
                    cells[day.Day] = new AdminCalendarCellViewModel
                    {
                        Status = "inactive",
                        Label = "Ngừng"
                    };
                    continue;
                }

                var booking = vehicle.Bookings
                    .Where(item =>
                        ActiveBookingStatuses.Contains(item.Status)
                        && item.PickupDate.Date <= day.Date
                        && item.ReturnDate.Date >= day.Date)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefault();

                cells[day.Day] = booking is null
                    ? new AdminCalendarCellViewModel()
                    : MapCalendarCell(booking);
            }

            return new AdminCalendarVehicleRowViewModel
            {
                VehicleId = vehicle.VehicleId,
                VehicleName = vehicle.VehicleName,
                LicensePlate = vehicle.LicensePlate,
                VehicleStatus = vehicle.Status,
                Cells = cells
            };
        }).ToList();

        return View(new AdminCalendarViewModel
        {
            Month = selectedMonth,
            Days = days,
            Vehicles = rows
        });
    }

    [HttpGet]
    public async Task<IActionResult> Operations(CancellationToken cancellationToken)
    {
        var start = DateTime.Today;
        var end = start.AddDays(7);
        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Vehicle)
            .Where(booking =>
                (booking.PickupDate >= start && booking.PickupDate < end)
                || (booking.ReturnDate >= start && booking.ReturnDate < end))
            .OrderBy(booking => booking.PickupDate)
            .ToListAsync(cancellationToken);

        var users = await LoadUsersAsync(
            bookings.Select(booking => booking.CustomerId),
            cancellationToken);

        var operations = bookings.SelectMany(booking =>
        {
            var user = GetUser(users, booking.CustomerId);
            var rows = new List<AdminOperationRowViewModel>();
            if (booking.PickupDate >= start && booking.PickupDate < end)
            {
                rows.Add(new AdminOperationRowViewModel
                {
                    BookingId = booking.BookingId,
                    Time = booking.PickupDate,
                    OperationType = "Giao xe",
                    VehicleName = booking.Vehicle.VehicleName,
                    LicensePlate = booking.Vehicle.LicensePlate,
                    CustomerName = user.FullName,
                    CustomerPhone = user.PhoneNumber,
                    Status = booking.Status
                });
            }

            if (booking.ReturnDate >= start && booking.ReturnDate < end)
            {
                rows.Add(new AdminOperationRowViewModel
                {
                    BookingId = booking.BookingId,
                    Time = booking.ReturnDate,
                    OperationType = "Nhận xe",
                    VehicleName = booking.Vehicle.VehicleName,
                    LicensePlate = booking.Vehicle.LicensePlate,
                    CustomerName = user.FullName,
                    CustomerPhone = user.PhoneNumber,
                    Status = booking.Status
                });
            }

            return rows;
        }).OrderBy(row => row.Time).ToList();

        return View(operations);
    }

    [HttpGet]
    public async Task<IActionResult> Customers(
        string? search,
        CancellationToken cancellationToken)
    {
        var customers = await _userManager.GetUsersInRoleAsync(RoleNames.Customer);
        var customerIds = customers.Select(customer => customer.Id).ToList();
        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => customerIds.Contains(booking.CustomerId))
            .ToListAsync(cancellationToken);
        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => customerIds.Contains(document.CustomerId))
            .ToListAsync(cancellationToken);

        var rows = customers.Select(customer => new AdminCustomerRowViewModel
        {
            CustomerId = customer.Id,
            FullName = customer.FullName,
            Email = customer.Email ?? string.Empty,
            PhoneNumber = customer.PhoneNumber ?? string.Empty,
            CreatedAt = customer.CreatedAt,
            IsActive = customer.IsActive,
            TotalBookings = bookings.Count(booking => booking.CustomerId == customer.Id),
            CompletedBookings = bookings.Count(booking =>
                booking.CustomerId == customer.Id
                && booking.Status == BookingStatus.Completed),
            VerifiedDocuments = documents.Count(document =>
                document.CustomerId == customer.Id
                && document.Status == DocumentStatus.Verified),
            PendingDocuments = documents.Count(document =>
                document.CustomerId == customer.Id
                && document.Status == DocumentStatus.Pending)
        }).OrderByDescending(customer => customer.CreatedAt).ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();
            rows = rows.Where(customer =>
                customer.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || customer.Email.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || customer.PhoneNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return View(new AdminCustomerListViewModel
        {
            Search = search,
            Customers = rows
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleCustomerStatus(
        string id,
        CancellationToken cancellationToken)
    {
        var customer = await _userManager.FindByIdAsync(id);
        if (customer is null)
        {
            return NotFound();
        }

        customer.IsActive = !customer.IsActive;
        await _userManager.UpdateAsync(customer);
        TempData["AdminSuccess"] = customer.IsActive
            ? "Đã mở lại tài khoản khách hàng."
            : "Đã tạm khóa tài khoản khách hàng.";
        return RedirectToAction(nameof(Customers));
    }

    [HttpGet]
    public async Task<IActionResult> Documents(
        DocumentStatus? status,
        CancellationToken cancellationToken)
    {
        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .OrderByDescending(document => document.CreatedAt)
            .ToListAsync(cancellationToken);

        if (status.HasValue)
        {
            documents = documents.Where(document => document.Status == status.Value).ToList();
        }

        var userLookup = await LoadUsersAsync(
            documents.Select(document => document.CustomerId),
            cancellationToken);

        return View(new AdminDocumentListViewModel
        {
            Status = status,
            Documents = documents.Select(document =>
            {
                var user = GetUser(userLookup, document.CustomerId);
                return new AdminDocumentRowViewModel
                {
                    CustomerDocumentId = document.CustomerDocumentId,
                    CustomerId = document.CustomerId,
                    CustomerName = user.FullName,
                    CustomerEmail = user.Email,
                    DocumentType = document.DocumentType,
                    DocumentNumber = document.DocumentNumber,
                    ExpiryDate = document.ExpiryDate,
                    ImagePath = NormalizeImagePath(document.ImagePath),
                    Status = document.Status,
                    RejectionReason = document.RejectionReason,
                    CreatedAt = document.CreatedAt
                };
            }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewDocument(
        int id,
        DocumentStatus newStatus,
        string? rejectionReason,
        CancellationToken cancellationToken)
    {
        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(item => item.CustomerDocumentId == id, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (newStatus == DocumentStatus.Rejected && string.IsNullOrWhiteSpace(rejectionReason))
        {
            TempData["AdminError"] = "Vui lòng nhập lý do khi yêu cầu khách bổ sung giấy tờ.";
            return RedirectToAction(nameof(Documents));
        }

        document.Status = newStatus;
        document.RejectionReason = newStatus == DocumentStatus.Rejected
            ? rejectionReason!.Trim()
            : null;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = document.CustomerId,
            Title = newStatus == DocumentStatus.Verified
                ? "Giấy tờ đã được SmartCar xác minh"
                : "Giấy tờ cần được cập nhật lại",
            Message = newStatus == DocumentStatus.Verified
                ? $"{GetDocumentName(document.DocumentType)} của bạn đã được duyệt."
                : $"{GetDocumentName(document.DocumentType)} chưa đạt yêu cầu: {document.RejectionReason}",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã cập nhật kết quả xác minh và thông báo cho khách hàng.";
        return RedirectToAction(nameof(Documents));
    }

    [HttpGet]
    public async Task<IActionResult> Payments(
        PaymentStatus? status,
        CancellationToken cancellationToken)
    {
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Include(payment => payment.Booking)
                .ThenInclude(booking => booking.Vehicle)
            .OrderByDescending(payment => payment.PaidAt ?? payment.Booking.CreatedAt)
            .ToListAsync(cancellationToken);

        var allPayments = payments.ToList();
        if (status.HasValue)
        {
            payments = payments.Where(payment => payment.Status == status.Value).ToList();
        }

        var users = await LoadUsersAsync(
            payments.Select(payment => payment.Booking.CustomerId),
            cancellationToken);

        return View(new AdminPaymentListViewModel
        {
            Status = status,
            TotalPaid = allPayments.Where(payment => payment.Status == PaymentStatus.Paid).Sum(payment => payment.Amount),
            TotalPending = allPayments.Where(payment => payment.Status == PaymentStatus.Pending).Sum(payment => payment.Amount),
            TotalRefunded = allPayments.Where(payment => payment.Status == PaymentStatus.Refunded).Sum(payment => payment.Amount),
            Payments = payments.Select(payment => new AdminPaymentRowViewModel
            {
                PaymentId = payment.PaymentId,
                BookingId = payment.BookingId,
                CustomerName = GetUser(users, payment.Booking.CustomerId).FullName,
                VehicleName = payment.Booking.Vehicle.VehicleName,
                Amount = payment.Amount,
                Method = payment.Method,
                Status = payment.Status,
                PaidAt = payment.PaidAt,
                TransactionCode = payment.TransactionCode
            }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefundPayment(
        int id,
        CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.PaymentId == id, cancellationToken);

        if (payment is null)
        {
            return NotFound();
        }

        if (payment.Status != PaymentStatus.Paid)
        {
            TempData["AdminError"] = "Chỉ giao dịch đã thanh toán mới có thể chuyển sang trạng thái hoàn tiền.";
            return RedirectToAction(nameof(Payments));
        }

        payment.Status = PaymentStatus.Refunded;
        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "SmartCar đã xử lý hoàn tiền",
            Message = $"Khoản tiền {payment.Amount:N0}đ của đơn #{payment.BookingId} đã được ghi nhận hoàn trả.",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã chuyển giao dịch sang trạng thái hoàn tiền.";
        return RedirectToAction(nameof(Payments));
    }

    [HttpGet]
    public async Task<IActionResult> Maintenance(CancellationToken cancellationToken)
    {
        var records = await _dbContext.MaintenanceRecords
            .AsNoTracking()
            .Include(record => record.Vehicle)
            .OrderByDescending(record => record.StartDate)
            .ToListAsync(cancellationToken);

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .OrderBy(vehicle => vehicle.VehicleName)
            .ToListAsync(cancellationToken);

        return View(new AdminMaintenanceListViewModel
        {
            Records = records.Select(record => new AdminMaintenanceRowViewModel
            {
                MaintenanceRecordId = record.MaintenanceRecordId,
                VehicleId = record.VehicleId,
                VehicleName = record.Vehicle.VehicleName,
                LicensePlate = record.Vehicle.LicensePlate,
                StartDate = record.StartDate,
                CompletedDate = record.CompletedDate,
                Content = record.Content,
                Cost = record.Cost,
                ServiceProvider = record.ServiceProvider,
                Mileage = record.Mileage,
                Status = record.Status
            }).ToList(),
            VehicleOptions = vehicles.Select(vehicle => new AdminVehicleOptionViewModel
            {
                VehicleId = vehicle.VehicleId,
                DisplayName = $"{vehicle.VehicleName} · {vehicle.LicensePlate}"
            }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateMaintenance(
        int vehicleId,
        DateTime startDate,
        string content,
        decimal cost,
        string? serviceProvider,
        int mileage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            TempData["AdminError"] = "Vui lòng nhập nội dung bảo trì.";
            return RedirectToAction(nameof(Maintenance));
        }

        var vehicle = await _dbContext.Vehicles
            .Include(item => item.Bookings)
            .FirstOrDefaultAsync(item => item.VehicleId == vehicleId, cancellationToken);

        if (vehicle is null)
        {
            return NotFound();
        }

        var hasCurrentRental = vehicle.Bookings.Any(booking =>
            booking.Status is BookingStatus.Rented or BookingStatus.PendingInspection);
        if (hasCurrentRental)
        {
            TempData["AdminError"] = "Xe đang trong chuyến thuê nên chưa thể đưa vào bảo trì.";
            return RedirectToAction(nameof(Maintenance));
        }

        _dbContext.MaintenanceRecords.Add(new MaintenanceRecord
        {
            VehicleId = vehicleId,
            StartDate = startDate == default ? DateTime.Today : startDate,
            Content = content.Trim(),
            Cost = Math.Max(0, cost),
            ServiceProvider = string.IsNullOrWhiteSpace(serviceProvider) ? null : serviceProvider.Trim(),
            Mileage = Math.Max(mileage, vehicle.CurrentMileage),
            Status = MaintenanceStatus.InProgress
        });
        vehicle.Status = VehicleStatus.Maintenance;

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã tạo phiếu bảo trì và khóa xe khỏi danh sách cho thuê.";
        return RedirectToAction(nameof(Maintenance));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteMaintenance(
        int id,
        CancellationToken cancellationToken)
    {
        var record = await _dbContext.MaintenanceRecords
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.MaintenanceRecordId == id, cancellationToken);

        if (record is null)
        {
            return NotFound();
        }

        record.Status = MaintenanceStatus.Completed;
        record.CompletedDate = DateTime.UtcNow;
        record.Vehicle.Status = VehicleStatus.Available;
        record.Vehicle.CurrentMileage = Math.Max(record.Vehicle.CurrentMileage, record.Mileage);

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã hoàn tất bảo trì và mở lại xe cho thuê.";
        return RedirectToAction(nameof(Maintenance));
    }

    private async Task<Dictionary<string, ApplicationUser>> LoadUsersAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        return await _userManager.Users
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);
    }

    private static (string FullName, string Email, string PhoneNumber) GetUser(
        IReadOnlyDictionary<string, ApplicationUser> users,
        string userId)
    {
        if (users.TryGetValue(userId, out var user))
        {
            return (
                string.IsNullOrWhiteSpace(user.FullName) ? "Khách hàng SmartCar" : user.FullName,
                user.Email ?? string.Empty,
                user.PhoneNumber ?? string.Empty);
        }

        return ("Khách hàng SmartCar", string.Empty, string.Empty);
    }

    private static AdminBookingRowViewModel MapBookingRow(
        Booking booking,
        IReadOnlyDictionary<string, ApplicationUser> users)
    {
        var user = GetUser(users, booking.CustomerId);
        return new AdminBookingRowViewModel
        {
            BookingId = booking.BookingId,
            CustomerName = user.FullName,
            CustomerEmail = user.Email,
            CustomerPhone = user.PhoneNumber,
            VehicleName = booking.Vehicle.VehicleName,
            LicensePlate = booking.Vehicle.LicensePlate,
            PickupDate = booking.PickupDate,
            ReturnDate = booking.ReturnDate,
            TotalAmount = booking.TotalAmount,
            Status = booking.Status,
            PaymentStatus = booking.Payment?.Status,
            CreatedAt = booking.CreatedAt,
            IsUrgent = booking.Status == BookingStatus.PendingConfirmation
                && booking.CreatedAt.AddMinutes(120) <= DateTime.UtcNow.AddMinutes(30)
        };
    }

    private static AdminCalendarCellViewModel MapCalendarCell(Booking booking) => booking.Status switch
    {
        BookingStatus.PendingConfirmation => new AdminCalendarCellViewModel
        {
            Status = "pending",
            Label = "Chờ duyệt",
            BookingId = booking.BookingId
        },
        BookingStatus.PendingPayment => new AdminCalendarCellViewModel
        {
            Status = "payment",
            Label = "Chờ cọc",
            BookingId = booking.BookingId
        },
        BookingStatus.Paid or BookingStatus.ReadyForPickup => new AdminCalendarCellViewModel
        {
            Status = "reserved",
            Label = "Đã đặt",
            BookingId = booking.BookingId
        },
        BookingStatus.Rented or BookingStatus.PendingInspection => new AdminCalendarCellViewModel
        {
            Status = "rented",
            Label = "Đang thuê",
            BookingId = booking.BookingId
        },
        _ => new AdminCalendarCellViewModel()
    };

    private static bool CanTransition(BookingStatus current, BookingStatus next)
    {
        if (next == BookingStatus.Cancelled
            && current is not BookingStatus.Completed and not BookingStatus.Rejected and not BookingStatus.Cancelled)
        {
            return true;
        }

        return current switch
        {
            BookingStatus.PendingConfirmation => next is BookingStatus.PendingPayment or BookingStatus.Rejected,
            BookingStatus.PendingPayment => next is BookingStatus.Paid,
            BookingStatus.Paid => next is BookingStatus.ReadyForPickup,
            BookingStatus.ReadyForPickup => next is BookingStatus.Rented,
            BookingStatus.Rented => next is BookingStatus.PendingInspection,
            BookingStatus.PendingInspection => next is BookingStatus.Completed,
            _ => false
        };
    }

    private static string GetBookingNotificationTitle(BookingStatus status) => status switch
    {
        BookingStatus.PendingPayment => "SmartCar đã xác nhận xe",
        BookingStatus.Paid => "SmartCar đã xác nhận thanh toán",
        BookingStatus.ReadyForPickup => "Xe đã sẵn sàng để giao nhận",
        BookingStatus.Rented => "Đã xác nhận khách nhận xe",
        BookingStatus.PendingInspection => "SmartCar đang kiểm tra xe sau chuyến",
        BookingStatus.Completed => "Đơn thuê đã hoàn thành",
        BookingStatus.Rejected => "Yêu cầu thuê xe bị từ chối",
        BookingStatus.Cancelled => "Đơn thuê đã bị hủy",
        _ => "Trạng thái đơn thuê đã thay đổi"
    };

    private static string GetBookingNotificationMessage(
        Booking booking,
        BookingStatus status,
        string? reason) => status switch
    {
        BookingStatus.PendingPayment => $"Xe {booking.Vehicle.VehicleName} đã được xác nhận. Vui lòng thanh toán cọc để giữ lịch.",
        BookingStatus.Paid => $"SmartCar đã ghi nhận tiền cọc cho đơn #{booking.BookingId}.",
        BookingStatus.ReadyForPickup => $"Xe {booking.Vehicle.VehicleName} đã được chuẩn bị. Vui lòng theo dõi thông tin giao nhận.",
        BookingStatus.Rented => $"SmartCar đã ghi nhận bạn nhận xe {booking.Vehicle.VehicleName}.",
        BookingStatus.PendingInspection => $"Xe đã được trả. SmartCar đang đối chiếu tình trạng và phụ phí nếu có.",
        BookingStatus.Completed => $"Đơn #{booking.BookingId} đã hoàn thành. Cảm ơn bạn đã sử dụng SmartCar.",
        BookingStatus.Rejected => $"Yêu cầu thuê xe chưa được chấp nhận. Lý do: {reason}",
        BookingStatus.Cancelled => $"Đơn thuê đã bị hủy. Lý do: {reason}",
        _ => $"Đơn #{booking.BookingId} đã được cập nhật."
    };

    private static string GetDocumentName(string documentType) => documentType.ToUpperInvariant() switch
    {
        "CCCD_FRONT" => "CCCD mặt trước",
        "CCCD_BACK" => "CCCD mặt sau",
        "DRIVER_LICENSE" => "Giấy phép lái xe",
        _ => "Giấy tờ"
    };

    private static string NormalizeImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return string.Empty;
        }

        if (imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith('/'))
        {
            return imagePath;
        }

        return "/" + imagePath.TrimStart('~', '/');
    }
}
