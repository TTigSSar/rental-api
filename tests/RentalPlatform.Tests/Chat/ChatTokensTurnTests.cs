using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Enums;
using Xunit;

namespace RentalPlatform.Tests.Chat;

public sealed class ChatTokensTurnTests
{
    public static IEnumerable<object[]> AllStatuses() =>
        Enum.GetValues<BookingStatus>().Select(status => new object[] { status });

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void TurnToken_Is_Owner_Only_For_Pending_And_Approved(BookingStatus status)
    {
        var expected = status is BookingStatus.Pending or BookingStatus.Approved ? "owner" : "none";

        Assert.Equal(expected, ChatTokens.TurnToken(status));
    }

    [Theory]
    [InlineData(ChatSystemKind.Rejected, "rejected")]
    [InlineData(ChatSystemKind.Cancelled, "cancelled")]
    public void SystemKindToken_Maps_New_Kinds(ChatSystemKind kind, string expected) =>
        Assert.Equal(expected, ChatTokens.SystemKindToken(kind));
}
