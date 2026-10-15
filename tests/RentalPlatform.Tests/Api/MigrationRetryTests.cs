using Xunit;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using RentalPlatform.Api.Extensions;

namespace RentalPlatform.Tests.Api;

public class MigrationRetryTests
{
    private static SqlException MakeSqlException(int number)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var error = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError).GetField("_number", all)!.SetValue(error, number);
        var errors = (SqlErrorCollection)RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection).GetField("_errors", all)!.SetValue(errors, new List<object> { error });
        var ex = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(SqlException).GetField("_errors", all)!.SetValue(ex, errors);
        return ex;
    }

    [Theory]
    [InlineData(1801, true)]
    [InlineData(4060, true)]
    [InlineData(18456, true)]
    [InlineData(547, false)]
    public void Classifier_matches_warm_up_errors_only(int number, bool expected) =>
        Assert.Equal(expected, MigrationRetry.IsStartupRetryable(MakeSqlException(number)));

    [Fact]
    public void Classifier_rejects_non_sql_exceptions() =>
        Assert.False(MigrationRetry.IsStartupRetryable(new InvalidOperationException()));

    private static Task Run(Func<CancellationToken, Task> action, Func<Exception, bool> retryable, int max, List<TimeSpan> delays) =>
        MigrationRetry.ExecuteAsync(action, retryable, max,
            (d, _) => { delays.Add(d); return Task.CompletedTask; }, TimeSpan.FromSeconds(5), (_, _, _) => { });

    [Fact]
    public async Task Transient_failure_then_success_retries_with_delay()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        await Run(_ => ++calls < 3 ? throw new TimeoutException() : Task.CompletedTask, _ => true, 10, delays);
        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Exhaustion_rethrows_original_after_max_attempts()
    {
        var calls = 0;
        var original = new TimeoutException("boom");
        var delays = new List<TimeSpan>();
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Run(_ => { calls++; throw original; }, _ => true, 4, delays));
        Assert.Same(original, thrown);
        Assert.Equal(4, calls);
        Assert.Equal(3, delays.Count);
    }

    [Fact]
    public async Task Non_retryable_throws_on_first_attempt_without_delay()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(_ => { calls++; throw new InvalidOperationException(); }, _ => false, 10, delays));
        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }
}
