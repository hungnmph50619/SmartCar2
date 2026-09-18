using SmartCar.Application.Features.Extensions;
using Xunit;

namespace SmartCar.Tests;

public sealed class ExtensionPaymentAtomicityTests
{
    [Fact]
    public void ExtensionServiceContract_DoesNotExposeStandaloneMarkPaidOperation()
    {
        Assert.DoesNotContain(
            typeof(IExtensionService).GetMethods(),
            method => string.Equals(
                method.Name,
                "MarkPaidAsync",
                StringComparison.Ordinal));
    }
}
