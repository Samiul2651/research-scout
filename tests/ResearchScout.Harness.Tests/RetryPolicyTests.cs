using System.Net;

namespace ResearchScout.Harness.Tests;

public class RetryPolicyTests
{
    private static HttpRequestException Http(HttpStatusCode status) => new("fail", null, status);

    [Fact]
    public async Task TransientFailures_AreRetried_UntilSuccess()
    {
        var waits = new List<TimeSpan>();
        var calls = 0;

        var (result, attempts) = await TestRetry.NoWait(waits).ExecuteAsync(_ =>
        {
            calls++;
            return calls < 3 ? throw Http(HttpStatusCode.TooManyRequests) : Task.FromResult("ok");
        }, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
        // Random fixed at 1.0 → no jitter: 1s after try 1, 2s after try 2.
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        var calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => TestRetry.NoWait().ExecuteAsync<string>(_ =>
        {
            calls++;
            throw Http(HttpStatusCode.Unauthorized);
        }, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GivesUp_AfterMaxAttempts()
    {
        var calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => TestRetry.NoWait(maxAttempts: 3).ExecuteAsync<string>(_ =>
        {
            calls++;
            throw Http(HttpStatusCode.ServiceUnavailable);
        }, CancellationToken.None));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void DelayFor_DoublesEachTime_CapsAtMax_AndJittersDownToHalf()
    {
        var full = new RetryPolicy { Random = () => 1.0, BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5) };
        var half = full.WithRandom(() => 0.0);

        Assert.Equal(TimeSpan.FromSeconds(1), full.DelayFor(1)); // 1 × 2^0
        Assert.Equal(TimeSpan.FromSeconds(4), full.DelayFor(3)); // 1 × 2^2
        Assert.Equal(TimeSpan.FromSeconds(5), full.DelayFor(4)); // 1 × 2^3 = 8, capped at 5
        Assert.Equal(TimeSpan.FromSeconds(2), half.DelayFor(3)); // 4 × 50%
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(529, true)] // Anthropic overloaded
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void IsTransientStatus(int status, bool expected) =>
        Assert.Equal(expected, RetryPolicy.IsTransientStatus(status));

    [Fact]
    public void Default_TreatsNetworkErrorsAndTimeoutsAsTransient()
    {
        Assert.True(RetryPolicy.DefaultIsTransient(new HttpRequestException("no route")));
        Assert.True(RetryPolicy.DefaultIsTransient(new TimeoutException()));
        Assert.True(RetryPolicy.DefaultIsTransient(new TaskCanceledException()));
        Assert.False(RetryPolicy.DefaultIsTransient(new InvalidOperationException()));
    }
}

internal static class RetryPolicyTestExtensions
{
    public static RetryPolicy WithRandom(this RetryPolicy policy, Func<double> random) => new()
    {
        MaxAttempts = policy.MaxAttempts,
        BaseDelay = policy.BaseDelay,
        MaxDelay = policy.MaxDelay,
        IsTransient = policy.IsTransient,
        Delay = policy.Delay,
        Random = random,
    };
}
