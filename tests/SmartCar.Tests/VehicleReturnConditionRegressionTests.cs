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
}
