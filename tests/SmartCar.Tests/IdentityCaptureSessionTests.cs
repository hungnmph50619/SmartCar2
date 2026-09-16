using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using Xunit;

namespace SmartCar.Tests;

public sealed class IdentityCaptureSessionTests
{
    [Fact]
    public void CanCapture_OnlyBeforeExpiryAndBeforeCompletion()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var session = new IdentityCaptureSession
        {
            ExpiresAt = now.AddMinutes(IdentityCapturePolicy.SessionLifetimeMinutes)
        };

        Assert.True(session.CanCapture(now));
        Assert.False(session.CanCapture(session.ExpiresAt));

        session.ImagePath = "secure-documents/customer/face.jpg";
        session.CompletedAt = now.AddMinutes(1);
        Assert.False(session.CanCapture(now.AddMinutes(2)));
    }

    [Fact]
    public void CanConsume_RequiresCompletedUnusedUnexpiredImage()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var session = new IdentityCaptureSession
        {
            ExpiresAt = now.AddMinutes(10),
            ImagePath = "secure-documents/customer/face.jpg",
            CompletedAt = now.AddMinutes(-1)
        };

        Assert.True(session.CanConsume(now));

        session.ConsumedAt = now;
        Assert.False(session.CanConsume(now.AddSeconds(1)));
    }
}
