using SmartCar.Domain.Entities;
using Xunit;

namespace SmartCar.Tests;

public sealed class VehicleReturnConditionRegressionTests
{
    [Fact]
    public void AccessoryStatus_IsIndependentFromInteriorCondition()
    {
        var vehicleReturn = new VehicleReturn
        {
            InteriorCondition = "Ghế sạch, taplo nguyên vẹn",
            AccessoryStatus = "Thiếu/mất: cáp sạc"
        };

        Assert.Equal("Ghế sạch, taplo nguyên vẹn", vehicleReturn.InteriorCondition);
        Assert.Equal("Thiếu/mất: cáp sạc", vehicleReturn.AccessoryStatus);
    }

    [Fact]
    public void StructuredAccessoryStatus_KeepsPlainNotesIndependent()
    {
        var vehicleReturn = new VehicleReturn
        {
            AccessoryStatus = "Thiếu/mất: cáp sạc",
            Notes = "Khách xác nhận vết xước cũ."
        };

        Assert.Equal("Thiếu/mất: cáp sạc", vehicleReturn.AccessoryStatus);
        Assert.Equal("Khách xác nhận vết xước cũ.", vehicleReturn.Notes);
    }

    [Fact]
    public void PlainNotes_DoNotInventAccessoryStatus()
    {
        var vehicleReturn = new VehicleReturn
        {
            Notes = "Không có ghi chú về phụ kiện."
        };

        Assert.Null(vehicleReturn.AccessoryStatus);
        Assert.Equal("Không có ghi chú về phụ kiện.", vehicleReturn.Notes);
    }

    [Fact]
    public void LegacyNotes_PopulateAccessoryStatusWithoutTouchingInterior()
    {
        var vehicleReturn = new VehicleReturn
        {
            InteriorCondition = "Nội thất sạch",
            Notes = "Phụ kiện khi trả: Thiếu/mất: cáp sạc | Ghi chú: khách đã xác nhận"
        };

        Assert.Equal("Thiếu/mất: cáp sạc", vehicleReturn.AccessoryStatus);
        Assert.Equal("Nội thất sạch", vehicleReturn.InteriorCondition);
    }
}
