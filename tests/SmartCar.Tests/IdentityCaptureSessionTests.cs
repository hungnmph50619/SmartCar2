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
    public void CanConsume_RequiresRecentCompletedUnusedImage()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var completedAt = now.AddMinutes(-1);
        var session = new IdentityCaptureSession
        {
            ExpiresAt = now.AddMinutes(1),
            ImagePath = "secure-documents/customer/face.jpg",
            CompletedAt = completedAt
        };

        Assert.True(session.CanConsume(now));
        Assert.True(session.CanConsume(
            completedAt.AddMinutes(IdentityCapturePolicy.SessionLifetimeMinutes).AddSeconds(-1)));
        Assert.False(session.CanConsume(
            completedAt.AddMinutes(IdentityCapturePolicy.SessionLifetimeMinutes)));
        Assert.False(session.CanConsume(now.AddHours(1)));

        session.ConsumedAt = now;
        Assert.False(session.CanConsume(now.AddSeconds(1)));
    }

    [Fact]
    public void CounterCitizenEvidence_HasDedicatedPurposes_AndCannotMasqueradeAsFaceCapture()
    {
        Assert.Contains(IdentityCapturePurposes.HandoverCitizenFront, IdentityCapturePurposes.All);
        Assert.Contains(IdentityCapturePurposes.HandoverCitizenBack, IdentityCapturePurposes.All);

        Assert.DoesNotContain(
            IdentityCaptureMethods.StaffCounterDocument,
            IdentityCaptureMethods.All);

        Assert.True(
            IdentityCapturePolicy.CounterDocumentSessionLifetimeMinutes >
            IdentityCapturePolicy.SessionLifetimeMinutes);
    }
}
