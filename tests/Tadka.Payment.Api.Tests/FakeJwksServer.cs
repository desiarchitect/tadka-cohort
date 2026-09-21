using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// A tiny in-memory stand-in for Tadka.Api's <c>/.well-known/jwks.json</c> (ADR-049) — an independent
/// TestServer, not Tadka.Api itself, so this suite can drive "rotate" / "drop a key" deterministically
/// without booting a second full service + its own Postgres. It serves whatever keys are currently in
/// <see cref="Keys"/>, in the exact JWK Set shape <see cref="Tadka.Payment.Api.Auth.JwksClient"/> consumes.
/// </summary>
public sealed class FakeJwksServer : IDisposable
{
    private readonly TestServer _server;

    /// <summary>Newest first — mirrors <c>SigningKeyStore</c>'s convention on the real service.</summary>
    public List<(string Kid, RSA Rsa)> Keys { get; } = [];

    public FakeJwksServer()
    {
        _server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services => services.AddRouting())
            .Configure(app => app.Run(async ctx =>
            {
                var doc = new { keys = Keys.Select(ToJwk).ToArray() };
                await ctx.Response.WriteAsJsonAsync(doc);
            })));
    }

    public HttpMessageHandler CreateHandler() => _server.CreateHandler();

    public (string Kid, RSA Rsa) AddKey()
    {
        var entry = (Kid: Guid.NewGuid().ToString("N"), Rsa: RSA.Create(2048));
        Keys.Insert(0, entry);
        return entry;
    }

    public void Drop(string kid) => Keys.RemoveAll(k => k.Kid == kid);

    private static object ToJwk((string Kid, RSA Rsa) key)
    {
        var p = key.Rsa.ExportParameters(includePrivateParameters: false);
        return new { kty = "RSA", use = "sig", kid = key.Kid, alg = "RS256", n = Base64Url(p.Modulus!), e = Base64Url(p.Exponent!) };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        foreach (var (_, rsa) in Keys) rsa.Dispose();
        _server.Dispose();
    }
}
