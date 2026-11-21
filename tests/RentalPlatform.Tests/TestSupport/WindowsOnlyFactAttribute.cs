using Xunit;

namespace RentalPlatform.Tests.TestSupport;

/// <summary>
/// A fact that needs Windows file-locking semantics (an open file cannot be deleted), used to force a real
/// IOException from File.Delete / Directory.Delete. Reported as SKIPPED with a reason elsewhere, never as passed.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows semantics: an open file cannot be deleted there, so the IO failure can be forced for real.";
        }
    }
}
