using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingExtensionStorageTests
{
    [Fact]
    public void CustomerNote_UsesUnlimitedColumnForForceMajeureEvidence()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new ApplicationDbContext(options);
        var property = context.Model
            .FindEntityType(typeof(BookingExtension))!
            .FindProperty(nameof(BookingExtension.CustomerNote))!;

        Assert.Equal(4000, property.GetMaxLength());
        Assert.Equal("nvarchar(4000)", property.GetColumnType());
    }
}
