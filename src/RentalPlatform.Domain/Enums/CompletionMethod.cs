namespace RentalPlatform.Domain.Enums;

/// <summary>How a booking reached the Completed state.</summary>
public enum CompletionMethod
{
    /// <summary>Both parties confirmed the return (the handshake completed).</summary>
    Mutual = 0,

    /// <summary>Owner-initiated return auto-completed after the 48h confirmation window elapsed.</summary>
    Auto = 1
}
