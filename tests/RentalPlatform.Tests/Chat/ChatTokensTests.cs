using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Enums;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// StatusToken derives the chat thread's booking-context status pill (ADR-001) from the
// linked booking's status + the conversation's ClosedAt override. See M-007: Completed
// bookings must resolve to a dedicated "completed" pill (not "active", and not "closed"
// either — the conversation only truly closes once ClosedAt is set, per M-010).
public sealed class ChatTokensTests
{
    private static readonly DateTime UtcNow = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(UtcNow);

    [Fact]
    public void Pending_Maps_To_Requested()
    {
        var token = ChatTokens.StatusToken(BookingStatus.Pending, Today.AddDays(5), closedAt: null, UtcNow);

        Assert.Equal("requested", token);
    }

    [Fact]
    public void Approved_Maps_To_Approved()
    {
        var token = ChatTokens.StatusToken(BookingStatus.Approved, Today.AddDays(5), closedAt: null, UtcNow);

        Assert.Equal("approved", token);
    }

    [Fact]
    public void Active_With_EndDate_Today_Or_Future_Maps_To_Active()
    {
        var token = ChatTokens.StatusToken(BookingStatus.Active, Today, closedAt: null, UtcNow);

        Assert.Equal("active", token);
    }

    [Fact]
    public void Active_With_EndDate_In_Past_Maps_To_ReturnDue()
    {
        var token = ChatTokens.StatusToken(BookingStatus.Active, Today.AddDays(-1), closedAt: null, UtcNow);

        Assert.Equal("return_due", token);
    }

    [Fact]
    public void Completed_With_Null_ClosedAt_Maps_To_Completed()
    {
        // M-007 regression: Completed previously mapped to "active", leaving the chat
        // header pill stuck on "Active" forever after the booking finished. It now maps
        // to its own "completed" token (not "closed") until the conversation actually
        // locks (ClosedAt set once both party reviews are in — see M-010).
        var token = ChatTokens.StatusToken(BookingStatus.Completed, Today.AddDays(-10), closedAt: null, UtcNow);

        Assert.Equal("completed", token);
    }

    [Fact]
    public void NonNull_ClosedAt_Overrides_To_Closed_Regardless_Of_Booking_Status()
    {
        var token = ChatTokens.StatusToken(BookingStatus.Active, Today.AddDays(5), closedAt: UtcNow, UtcNow);

        Assert.Equal("closed", token);
    }

    [Fact]
    public void Completed_With_NonNull_ClosedAt_Still_Maps_To_Closed()
    {
        // The ClosedAt override wins even for a Completed booking — "completed" only
        // applies while ClosedAt is still null.
        var token = ChatTokens.StatusToken(BookingStatus.Completed, Today.AddDays(-10), closedAt: UtcNow, UtcNow);

        Assert.Equal("closed", token);
    }

    [Theory]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public void Terminal_NonCompleted_Statuses_Fall_Back_To_Requested(BookingStatus status)
    {
        var token = ChatTokens.StatusToken(status, Today.AddDays(5), closedAt: null, UtcNow);

        Assert.Equal("requested", token);
    }
}
