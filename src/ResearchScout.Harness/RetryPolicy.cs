using System.Net;

namespace ResearchScout.Harness;

/// <summary>
/// Retries model calls that failed for a temporary reason (rate limit, overload, network, timeout).
/// The harness does not know provider SDK exception types, so the caller passes <see cref="IsTransient"/>
/// (the Llm project provides one that knows every SDK).
///
/// Wait before retry n (n = 1, 2, ...): BaseDelay × 2^(n-1), capped at MaxDelay, then scaled by a random 50-100%
/// so many clients don't all retry at the same moment. With the defaults: ~1s, ~2s.
/// </summary>
public sealed class RetryPolicy
{
    public static readonly RetryPolicy None = new() { MaxAttempts = 1 };

    /// <summary>Total tries including the first one. 3 = one call + up to two retries.</summary>
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Decides whether an exception is worth retrying. Default: network errors, timeouts, HTTP 408/429/5xx.</summary>
    public Func<Exception, bool> IsTransient { get; init; } = DefaultIsTransient;

    /// <summary>How to wait. Tests replace it so they don't actually sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Random factor source for jitter, 0.0-1.0. Tests replace it to get fixed waits.</summary>
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    /// <summary>Runs <paramref name="action"/>, retrying transient failures. Returns the result and how many tries it took.</summary>
    public async Task<(T Result, int Attempts)> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await action(cancellationToken).ConfigureAwait(false), attempt);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Delay(DelayFor(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Wait after failed try number <paramref name="attempt"/>.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        var jitter = 0.5 + (Random() * 0.5);
        return TimeSpan.FromMilliseconds(capped * jitter);
    }

    public static bool DefaultIsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true, // no response at all: network problem
        HttpRequestException { StatusCode: { } status } => IsTransientStatus((int)status),
        TimeoutException => true,
        OperationCanceledException => true, // only reached when the caller did NOT cancel: an SDK timeout
        _ => false,
    };

    /// <summary>408 request timeout, 429 rate limit, 5xx server errors (including Anthropic's 529 overloaded).</summary>
    public static bool IsTransientStatus(int status) =>
        status is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests or >= 500;
}
