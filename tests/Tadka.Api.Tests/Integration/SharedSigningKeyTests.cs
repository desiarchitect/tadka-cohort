using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Tadka.Api.Auth;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Shared signing key (ADR-067): behind the scale-out load balancer every replica must sign with, and publish,
/// the SAME key, or a token issued by replica A is rejected by replica B. These tests stand in for two replicas
/// with two <see cref="SigningKeyStore"/> instances built from the same configured key.
/// </summary>
public class SharedSigningKeyTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;
    private static readonly JsonWebTokenHandler Handler = new();

    private static string NewPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    [Fact]
    public void Two_replicas_built_from_the_same_key_agree_on_the_kid_and_verify_each_others_signatures()
    {
        var pem = NewPem();
        var replicaA = new SigningKeyStore(pem);
        var replicaB = new SigningKeyStore(pem);

        Assert.Equal(replicaA.Current.Kid, replicaB.Current.Kid);

        var data = "payload"u8.ToArray();
        var signature = replicaA.Current.Rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var keyOnB = replicaB.Find(replicaA.Current.Kid);
        Assert.NotNull(keyOnB);
        Assert.True(keyOnB!.Rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Generated_stores_on_two_replicas_do_NOT_agree_which_is_why_shared_mode_exists()
    {
        Assert.NotEqual(new SigningKeyStore().Current.Kid, new SigningKeyStore().Current.Kid);
    }

    [Fact]
    public void A_base64_pkcs8_key_loads_to_the_same_kid_as_the_PEM_form()
    {
        using var rsa = RSA.Create(2048);
        var fromPem = new SigningKeyStore(rsa.ExportPkcs8PrivateKeyPem());
        var fromBase64 = new SigningKeyStore(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
        Assert.Equal(fromPem.Current.Kid, fromBase64.Current.Kid);
    }

    [Fact]
    public void The_previous_key_keeps_verifying_during_a_rollover_and_rotation_on_one_replica_is_refused()
    {
        var store = new SigningKeyStore(NewPem(), previousPrivateKey: NewPem());

        Assert.Equal(2, store.AllForVerification.Count);
        Assert.NotEqual(store.AllForVerification[0].Kid, store.AllForVerification[1].Kid);
        Assert.Throws<InvalidOperationException>(() => store.Rotate());
    }

    [Fact]
    public async Task With_a_shared_key_configured_the_published_JWKS_and_issued_tokens_use_it_and_the_rotate_endpoint_returns_409()
    {
        var pem = NewPem();
        var expectedKid = new SigningKeyStore(pem).Current.Kid;
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Jwt:SigningKeyPem", pem)).CreateClient();

        var jwks = await (await client.GetAsync("/.well-known/jwks.json")).Content.ReadFromJsonAsync<JwksDocument>();
        Assert.Equal(expectedKid, Assert.Single(jwks!.Keys).Kid);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["accessToken"]!.ToString()!;
        Assert.Equal(expectedKid, Handler.ReadJsonWebToken(token).Kid);

        var rotate = await client.PostAsync("/api/v1/auth/rotate-signing-key", null); // TestAuthHandler: Admin by default
        Assert.Equal(HttpStatusCode.Conflict, rotate.StatusCode);
    }
}
