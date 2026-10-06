using Xunit;

namespace RentalPlatform.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that can only run against a real SQL Server
/// (<see cref="SqlServerTestDatabase"/>), typically because the behaviour under test is T-SQL,
/// so running it on SQLite would be testing a different program.
/// </summary>
/// <remarks>
/// When no SQL Server is reachable the test is reported as SKIPPED, with a reason, through the
/// real xUnit mechanism. That is the whole point: these tests used to guard a missing SQL Server
/// with an early <c>return</c>, which reported them as PASSED - green coverage that had not run,
/// on exactly the machine where someone might be about to ship an irreversible data migration.
///
/// xUnit 2.9.2 (this suite's version) has no dynamic skip - <c>Assert.Skip</c> arrived in xUnit
/// v3 - so the skip is decided when the attribute is constructed, at discovery time, from
/// <see cref="SqlServerTestDatabase.IsAvailable"/>. It is still a conditional skip and not a
/// hardcoded one: where SQL Server is present, <see cref="FactAttribute.Skip"/> stays null and
/// the test runs for real.
///
/// Not every SQL Server test uses this attribute, and that asymmetry is deliberate.
/// ConversationsStoreModerationConcurrencyTests keeps a plain [Fact] and so FAILS when no SQL
/// Server is reachable. Failing loudly is strictly stronger protection than skipping, and those
/// tests never reported a false pass, so there was nothing to fix there - the reason this
/// attribute exists is the early-return that reported tests as PASSED. Do not "unify" the two by
/// weakening those tests from failing to skipped.
/// </remarks>
public sealed class SqlServerFactAttribute : FactAttribute
{
    internal const string SkipReason =
        "Requires a local SQL Server on Server=. (or a connection string in "
        + SqlServerTestDatabase.ConnectionStringEnvironmentVariable
        + ") - the behaviour under test is T-SQL and cannot be verified on SQLite.";

    public SqlServerFactAttribute()
    {
        // Skip ONLY when no SQL Server was declared for this machine. If
        // RENTALPLATFORM_TEST_SQLSERVER_CONNECTION_STRING is set (CI, per ADR-022) the environment
        // has promised a SQL Server, so an unreachable one is a broken environment rather than an
        // absent dependency: leave Skip null, let the test run, and let it fail on the real
        // connection error. See SqlServerTestDatabase.IsExplicitlyConfigured for the full contract.
        if (!SqlServerTestDatabase.IsAvailable && !SqlServerTestDatabase.IsExplicitlyConfigured)
        {
            Skip = SkipReason;
        }
    }
}
