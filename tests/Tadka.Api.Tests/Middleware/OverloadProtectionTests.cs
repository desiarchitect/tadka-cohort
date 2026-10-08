using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Tadka.Api.Middleware;

namespace Tadka.Api.Tests.Middleware;

/// <summary>
/// The two overload levers taught on Day 14, tested without a server: admission control (ADR-059) answers
/// 429 + Retry-After when too many requests are already in flight, and priority load shedding (ADR-060)
/// answers 503 + Retry-After for non-critical paths while ordering, payment and health keep working.
/// </summary>
public class OverloadProtectionTests
{
    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static DefaultHttpContext Ctx(string path, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static LoadSheddingMiddleware Shedder(bool enabled, Action onReached) =>
        new(_ => { onReached(); return Task.CompletedTask; },
            new FixedMonitor<LoadSheddingOptions>(new LoadSheddingOptions { Enabled = enabled }));

    [Theory]
    [InlineData("/api/v1/orders/history")]
    [InlineData("/api/v1/orders/3f2c1f7e-0b52-4f5a-9d3e-0a1b2c3d4e5f/invoice")]
    [InlineData("/api/v1/coupons/WELCOME10")]
    [InlineData("/api/v1/flags/UseNewMenuPath")]
    public async Task When_shedding_is_on_non_critical_paths_get_503_with_Retry_After(string path)
    {
        var reached = false;
        var ctx = Ctx(path);

        await Shedder(enabled: true, () => reached = true).InvokeAsync(ctx);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Equal("5", ctx.Response.Headers.RetryAfter.ToString());
    }

    [Theory]
    [InlineData("/health", "GET")]
    [InlineData("/api/v1/auth/login", "POST")]
    [InlineData("/api/v1/orders", "POST")]
    [InlineData("/api/v1/orders/3f2c1f7e-0b52-4f5a-9d3e-0a1b2c3d4e5f", "GET")]
    [InlineData("/api/v1/payments/3f2c1f7e-0b52-4f5a-9d3e-0a1b2c3d4e5f", "GET")]
    [InlineData("/api/v1/deliveries/3f2c1f7e-0b52-4f5a-9d3e-0a1b2c3d4e5f/track", "GET")]
    public async Task When_shedding_is_on_critical_paths_are_still_admitted(string path, string method)
    {
        var reached = false;
        var ctx = Ctx(path, method);

        await Shedder(enabled: true, () => reached = true).InvokeAsync(ctx);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task When_shedding_is_off_nothing_is_shed()
    {
        var reached = false;

        await Shedder(enabled: false, () => reached = true).InvokeAsync(Ctx("/api/v1/orders/history"));

        Assert.True(reached);
    }

    [Fact]
    public async Task Admission_control_rejects_the_request_over_the_limit_with_429_and_admits_again_once_one_finishes()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var middleware = new BackpressureMiddleware(
            async _ => { entered.TrySetResult(); await release.Task; },
            Options.Create(new BackpressureOptions { MaxConcurrent = 1 }));

        var first = middleware.InvokeAsync(Ctx("/api/v1/orders/abc"));          // takes the only slot
        await entered.Task;

        var second = Ctx("/api/v1/orders/abc");
        await middleware.InvokeAsync(second);                                      // slot taken: refused, not queued
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("1", second.Response.Headers.RetryAfter.ToString());

        release.SetResult();
        await first;

        var third = Ctx("/api/v1/orders/abc");                                     // slot free again
        await middleware.InvokeAsync(third);
        Assert.Equal(StatusCodes.Status200OK, third.Response.StatusCode);
    }
}
