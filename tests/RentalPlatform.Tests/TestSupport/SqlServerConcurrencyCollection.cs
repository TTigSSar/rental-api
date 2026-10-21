using Xunit;

namespace RentalPlatform.Tests.TestSupport;

/// <summary>
/// One xUnit collection for every test class that races several writers against the real SQL
/// Server. xUnit runs the classes of a collection one after another and the collections in
/// parallel, so tagging these classes keeps their bursts of lock-blocked sessions from stacking up
/// on the single shared instance.
/// </summary>
/// <remarks>
/// Why this exists (M-XXX, the EmailVerificationConcurrencyTests stalls): a session blocked on a
/// lock pins a SQL Server worker thread, and a developer-machine instance can be slow to create the
/// next one. The lock holder's NEXT batch then sits PENDING in THREADPOOL for the whole 30 s command
/// timeout while everything queued behind it times out - a starvation stall, not a deadlock. The
/// bound on a single test lives in that test (how many racers it fires at once); this collection
/// bounds the sum across classes, which otherwise run concurrently.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SqlServerConcurrencyCollection
{
    public const string Name = "SqlServerConcurrency";
}
