using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Gateway.Security;

namespace Tadka.Gateway.Tests;

/// <summary>
/// Origin lockdown (ADR-064): with Gateway:RequiredFrontDoorId unset the gateway behaves exactly as before;
/// with it set, only requests carrying the matching X-Azure-FDID get through, except the health probes and
/// the SSE path. Boots the REAL gateway pipeline in memory. No Docker, no downstream services: the YARP
/// routes that would match /api/v1/* are moved out of the way, and a terminal "reached the app" endpoint
/// stands in for "the request passed the lock".
/// </summary>
public class FrontDoorOriginLockTests
{
    private const string FdId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string Reached = "reached-app";

    private static WebApplicationFactory<Program> CreateFactory(string? requiredFrontDoorId) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (requiredFrontDoorId is not null)
                b.UseSetting(FrontDoorOriginLock.ConfigKey, requiredFrontDoorId);

            // Keep /api/v1/** off the proxy so nothing tries to reach a real service.
            b.UseSetting("ReverseProxy:Routes:monolith:Match:Path", "/__test-disabled/{**remainder}");

            // Anything that passes the lock and matches no endpoint lands here.
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, ReachedAppFilter>());
        });

    [Fact]
    public async Task Off_by_default_request_without_header_passes()
    {
        using var factory = CreateFactory(null);
        var res = await factory.CreateClient().GetAsync("/api/v1/orders/mine");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Reached, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Off_when_setting_is_empty()
    {
        using var factory = CreateFactory("");
        var res = await factory.CreateClient().GetAsync("/api/v1/orders/mine");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task On_request_without_header_is_403()
    {
        using var factory = CreateFactory(FdId);
        var res = await factory.CreateClient().GetAsync("/api/v1/orders/mine");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task On_request_with_wrong_header_is_403()
    {
        using var factory = CreateFactory(FdId);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders/mine");
        req.Headers.Add(FrontDoorOriginLock.HeaderName, Guid.NewGuid().ToString());

        var res = await factory.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task On_request_with_matching_header_passes()
    {
        using var factory = CreateFactory(FdId);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders/mine");
        req.Headers.Add(FrontDoorOriginLock.HeaderName, FdId.ToUpperInvariant());

        var res = await factory.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Reached, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task On_health_probes_are_exempt(string path)
    {
        using var factory = CreateFactory(FdId);
        var res = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task On_sse_path_is_exempt()
    {
        using var factory = CreateFactory(FdId);
        var res = await factory.CreateClient().GetAsync("/api/v1/orders/8d3c1f7e-0000-4000-8000-000000000001/events");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Reached, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/orders/abc/events", true)]
    [InlineData("/api/v1/orders/abc/events/", true)]
    [InlineData("/health", true)]
    [InlineData("/api/v1/orders/abc", false)]
    [InlineData("/api/v1/orders/abc/events/extra", false)]
    [InlineData("/api/v1/orders/a/b/events", false)]
    [InlineData("/healthz", false)]
    public void Exempt_paths_are_exact(string path, bool exempt) =>
        Assert.Equal(exempt, FrontDoorOriginLock.IsExempt(new PathString(path)));

    private sealed class ReachedAppFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(ctx => ctx.Response.WriteAsync(Reached));
        };
    }
}
