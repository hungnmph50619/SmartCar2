using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Identity;

namespace SmartCar.Infrastructure.Persistence;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<VehicleImage> VehicleImages => Set<VehicleImage>();
    public DbSet<CustomerDocument> CustomerDocuments => Set<CustomerDocument>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingExtension> BookingExtensions => Set<BookingExtension>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<VehicleHandover> VehicleHandovers => Set<VehicleHandover>();
    public DbSet<VehicleReturn> VehicleReturns => Set<VehicleReturn>();
    public DbSet<AdditionalCharge> AdditionalCharges => Set<AdditionalCharge>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.FullName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.Address).HasMaxLength(250);
            entity.Property(user => user.AvatarPath).HasMaxLength(250);
            entity.HasIndex(user => user.PhoneNumber)
                .IsUnique()
                .HasFilter("[PhoneNumber] IS NOT NULL");
        });

        builder.Entity<Brand>(entity =>
        {
            entity.HasKey(brand => brand.BrandId);
            entity.Property(brand => brand.BrandName).HasMaxLength(100).IsRequired();
            entity.HasIndex(brand => brand.BrandName).IsUnique();
        });

        builder.Entity<Vehicle>(entity =>
        {
            entity.HasKey(vehicle => vehicle.VehicleId);
            entity.Property(vehicle => vehicle.VehicleName).HasMaxLength(150).IsRequired();
            entity.Property(vehicle => vehicle.Model).HasMaxLength(100);
            entity.Property(vehicle => vehicle.LicensePlate).HasMaxLength(20).IsRequired();
            entity.Property(vehicle => vehicle.Transmission).HasMaxLength(30).IsRequired();
            entity.Property(vehicle => vehicle.FuelType).HasMaxLength(30).IsRequired();
            entity.Property(vehicle => vehicle.Color).HasMaxLength(50);
            entity.Property(vehicle => vehicle.DailyPrice).HasPrecision(18, 2);
            entity.Property(vehicle => vehicle.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(vehicle => vehicle.RowVersion).IsRowVersion();
            entity.HasIndex(vehicle => vehicle.LicensePlate).IsUnique();
            entity.HasOne(vehicle => vehicle.Brand)
                .WithMany(brand => brand.Vehicles)
                .HasForeignKey(vehicle => vehicle.BrandId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<VehicleImage>(entity =>
        {
            entity.HasKey(image => image.VehicleImageId);
            entity.Property(image => image.ImagePath).HasMaxLength(300).IsRequired();
            entity.HasOne(image => image.Vehicle)
                .WithMany(vehicle => vehicle.Images)
                .HasForeignKey(image => image.VehicleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CustomerDocument>(entity =>
        {
            entity.HasKey(document => document.CustomerDocumentId);
            entity.Property(document => document.CustomerId).HasMaxLength(450).IsRequired();
            entity.Property(document => document.DocumentType).HasMaxLength(30).IsRequired();
            entity.Property(document => document.DocumentNumber).HasMaxLength(50).IsRequired();
            entity.Property(document => document.ImagePath).HasMaxLength(300).IsRequired();
            entity.Property(document => document.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(document => document.RejectionReason).HasMaxLength(500);
            entity.Property(document => document.VerifiedBy).HasMaxLength(450);
            entity.HasIndex(document => new { document.CustomerId, document.DocumentType }).IsUnique();
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(document => document.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Booking>(entity =>
        {
            entity.HasKey(booking => booking.BookingId);
            entity.Property(booking => booking.CustomerId).HasMaxLength(450).IsRequired();
            entity.Property(booking => booking.DailyPrice).HasPrecision(18, 2);
            entity.Property(booking => booking.RentalAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.AdditionalAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.TotalAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.RefundAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.CancelReason).HasMaxLength(500);
            entity.Property(booking => booking.CancelledBy).HasMaxLength(30);
            entity.Property(booking => booking.RefundReason).HasMaxLength(500);
            entity.Property(booking => booking.RowVersion).IsRowVersion();
            entity.HasIndex(booking => new
            {
                booking.VehicleId,
                booking.PickupDate,
                booking.ReturnDate,
                booking.Status
            });
            entity.HasOne(booking => booking.Vehicle)
                .WithMany(vehicle => vehicle.Bookings)
                .HasForeignKey(booking => booking.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(booking => booking.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookingExtension>(entity =>
        {
            entity.HasKey(extension => extension.BookingExtensionId);
            entity.Property(extension => extension.AdditionalAmount).HasPrecision(18, 2);
            entity.Property(extension => extension.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(extension => extension.CustomerNote).HasMaxLength(500);
            entity.Property(extension => extension.AdminNote).HasMaxLength(500);
            entity.HasIndex(extension => new { extension.BookingId, extension.Status });
            entity.HasOne(extension => extension.Booking)
                .WithMany(booking => booking.Extensions)
                .HasForeignKey(extension => extension.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Payment>(entity =>
        {
            entity.HasKey(payment => payment.PaymentId);
            entity.Property(payment => payment.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(payment => payment.Amount).HasPrecision(18, 2);
            entity.Property(payment => payment.Method).HasMaxLength(50).IsRequired();
            entity.Property(payment => payment.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(payment => payment.TransactionCode).HasMaxLength(100);
            entity.HasIndex(payment => new { payment.BookingId, payment.Type, payment.Status });
            entity.HasOne(payment => payment.Booking)
                .WithMany(booking => booking.Payments)
                .HasForeignKey(payment => payment.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<VehicleHandover>(entity =>
        {
            entity.HasKey(handover => handover.VehicleHandoverId);
            entity.Property(handover => handover.FuelLevel).HasMaxLength(30).IsRequired();
            entity.HasIndex(handover => handover.BookingId).IsUnique();
            entity.HasOne(handover => handover.Booking)
                .WithOne(booking => booking.Handover)
                .HasForeignKey<VehicleHandover>(handover => handover.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<VehicleReturn>(entity =>
        {
            entity.HasKey(vehicleReturn => vehicleReturn.VehicleReturnId);
            entity.Property(vehicleReturn => vehicleReturn.FuelLevel).HasMaxLength(30).IsRequired();
            entity.Property(vehicleReturn => vehicleReturn.LateFee).HasPrecision(18, 2);
            entity.HasIndex(vehicleReturn => vehicleReturn.BookingId).IsUnique();
            entity.HasOne(vehicleReturn => vehicleReturn.Booking)
                .WithOne(booking => booking.VehicleReturn)
                .HasForeignKey<VehicleReturn>(vehicleReturn => vehicleReturn.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AdditionalCharge>(entity =>
        {
            entity.HasKey(charge => charge.AdditionalChargeId);
            entity.Property(charge => charge.ChargeType).HasConversion<string>().HasMaxLength(30);
            entity.Property(charge => charge.Description).HasMaxLength(250).IsRequired();
            entity.Property(charge => charge.Amount).HasPrecision(18, 2);
            entity.HasOne(charge => charge.VehicleReturn)
                .WithMany(vehicleReturn => vehicleReturn.AdditionalCharges)
                .HasForeignKey(charge => charge.VehicleReturnId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MaintenanceRecord>(entity =>
        {
            entity.HasKey(record => record.MaintenanceRecordId);
            entity.Property(record => record.Content).HasMaxLength(1000).IsRequired();
            entity.Property(record => record.Cost).HasPrecision(18, 2);
            entity.Property(record => record.ServiceProvider).HasMaxLength(200);
            entity.Property(record => record.Status).HasConversion<string>().HasMaxLength(30);
            entity.HasOne(record => record.Vehicle)
                .WithMany(vehicle => vehicle.MaintenanceRecords)
                .HasForeignKey(record => record.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Review>(entity =>
        {
            entity.HasKey(review => review.ReviewId);
            entity.Property(review => review.Comment).HasMaxLength(1000);
            entity.HasIndex(review => review.BookingId).IsUnique();
            entity.HasOne(review => review.Booking)
                .WithOne(booking => booking.Review)
                .HasForeignKey<Review>(review => review.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Notification>(entity =>
        {
            entity.HasKey(notification => notification.NotificationId);
            entity.Property(notification => notification.UserId).HasMaxLength(450).IsRequired();
            entity.Property(notification => notification.Title).HasMaxLength(200).IsRequired();
            entity.Property(notification => notification.Message).HasMaxLength(1000).IsRequired();
            entity.HasIndex(notification => new { notification.UserId, notification.IsRead });
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(notification => notification.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
