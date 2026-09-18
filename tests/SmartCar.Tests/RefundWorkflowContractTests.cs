using SmartCar.Application.Features.Payments;
using Xunit;

namespace SmartCar.Tests;

public sealed class RefundWorkflowContractTests
{
    [Fact]
    public void PaymentServiceContract_DoesNotExposeDirectRefundFinalization()
    {
        Assert.DoesNotContain(
            typeof(IPaymentService).GetMethods(),
            method => string.Equals(
                method.Name,
                "ConfirmRefundAsync",
                StringComparison.Ordinal));
    }
}
