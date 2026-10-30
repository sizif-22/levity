using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Levity.Services;
using Xunit;

public class WebPushTests
{
    private sealed class FakePushServer : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<Uri, HttpStatusCode> Reply { get; set; } = _ => HttpStatusCode.Created;
        public List<long> BodyLengths { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            BodyLengths.Add(request.Content == null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct)).Length);
            return new HttpResponseMessage(Reply(request.RequestUri!));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    private const string Chrome = "https://fcm.googleapis.com/fcm/send/abc123";
    private const string Apple = "https://web.push.apple.com/QWERTY";
    private const string Firefox = "https://updates.push.services.mozilla.com/wpush/v2/xyz";

    private static string Dir() => Path.Combine(Path.GetTempPath(), "push-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(Chrome, true)]
    [InlineData(Apple, true)]
    [InlineData(Firefox, true)]
    [InlineData("https://wns2-par02p.notify.windows.com/?token=1", true)]
    [InlineData("http://fcm.googleapis.com/x", false)]
    [InlineData("https://evil.example.com/fcm.googleapis.com", false)]
    [InlineData("https://fcm.googleapis.com.evil.com/x", false)]
    [InlineData("https://notfcm.googleapis.com.attacker.io/x", false)]
    [InlineData("https://user:pw@fcm.googleapis.com/x", false)]
    [InlineData("https://fcm.googleapis.com:8443/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("file:///c:/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_real_push_services_over_https_are_accepted(string? endpoint, bool ok) => Assert.Equal(ok, WebPushRules.IsAllowedEndpoint(endpoint));

    [Fact]
    public void A_very_long_endpoint_is_refused() => Assert.False(WebPushRules.IsAllowedEndpoint(Chrome + new string('a', 3000)));

    [Fact]
    public void Quiet_rules_push_only_when_enabled_nobody_is_connected_and_not_too_often()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(WebPushRules.ShouldNotify(true, 0, null, now));
        Assert.False(WebPushRules.ShouldNotify(false, 0, null, now));
        Assert.False(WebPushRules.ShouldNotify(true, 1, null, now));
        Assert.False(WebPushRules.ShouldNotify(true, 0, now.AddSeconds(-5), now));
        Assert.True(WebPushRules.ShouldNotify(true, 0, now.AddSeconds(-25), now));
    }

    [Fact]
    public void Base64url_round_trips_with_and_without_padding()
    {
        foreach (int n in new[] { 1, 2, 3, 32, 65 })
        {
            var data = RandomNumberGenerator.GetBytes(n);
            string s = WebPushRules.Base64Url(data);
            Assert.DoesNotContain('=', s);
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
            Assert.Equal(data, WebPushRules.FromBase64Url(s));
        }
    }

    [Fact]
    public void The_public_key_is_a_65_byte_uncompressed_point()
    {
        var k = VapidKeys.Generate();
        var raw = WebPushRules.FromBase64Url(k.PublicKey);
        Assert.Equal(65, raw.Length);
        Assert.Equal(4, raw[0]);
    }

    [Fact]
    public void The_authorization_is_a_valid_signed_vapid_token_for_the_push_services_origin()
    {
        var k = VapidKeys.Generate();
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        string auth = k.AuthorizationFor(Chrome, "mailto:a@example.com", now);
        Assert.StartsWith("vapid t=", auth);
        Assert.EndsWith(", k=" + k.PublicKey, auth);
        string jwt = auth.Substring("vapid t=".Length, auth.IndexOf(", k=") - "vapid t=".Length);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("ES256", JsonDocument.Parse(WebPushRules.FromBase64Url(parts[0])).RootElement.GetProperty("alg").GetString());
        var claims = JsonDocument.Parse(WebPushRules.FromBase64Url(parts[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal("mailto:a@example.com", claims.GetProperty("sub").GetString());
        long exp = claims.GetProperty("exp").GetInt64();
        long t = new DateTimeOffset(now).ToUnixTimeSeconds();
        Assert.InRange(exp - t, 1, 24 * 3600 - 1);   // the push services reject 24 hours or more

        // verify the signature with only the public key, as a push service does
        var raw = WebPushRules.FromBase64Url(k.PublicKey);
        using var pub = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = raw[1..33], Y = raw[33..65] } });
        var sig = WebPushRules.FromBase64Url(parts[2]);
        Assert.Equal(64, sig.Length);
        Assert.True(pub.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void The_key_survives_a_restart_so_phones_stay_subscribed()
    {
        string dir = Dir();
        using var a = new PushService(dir);
        Assert.Null(a.Subscribe("dev1", Chrome));
        string key = a.PublicKey;
        using var b = new PushService(dir);
        Assert.Equal(key, b.PublicKey);
        Assert.True(b.IsSubscribed("dev1"));
    }

    [Fact]
    public void A_corrupt_file_makes_a_fresh_key_instead_of_crashing()
    {
        string dir = Dir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "push.json"), "{ not json");
        using var s = new PushService(dir);
        Assert.Equal(65, WebPushRules.FromBase64Url(s.PublicKey).Length);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Subscribing_checks_the_address_limits_per_device_and_replaces_a_repeat()
    {
        using var s = new PushService(Dir());
        Assert.NotNull(s.Subscribe("dev1", "https://evil.example.com/x"));
        Assert.NotNull(s.Subscribe("", Chrome));
        Assert.Null(s.Subscribe("dev1", Chrome));
        Assert.Null(s.Subscribe("dev1", Chrome));
        Assert.Equal(1, s.Count);
        for (int i = 0; i < 5; i++) Assert.Null(s.Subscribe("dev1", Chrome + i));
        Assert.Equal(WebPushRules.MaxPerDevice, s.Count);   // the oldest are dropped
    }

    [Fact]
    public void The_total_number_of_subscriptions_is_capped()
    {
        using var s = new PushService(Dir());
        for (int i = 0; i < WebPushRules.MaxTotal; i++) Assert.Null(s.Subscribe("dev" + i, Chrome + i));
        Assert.NotNull(s.Subscribe("one-more", Chrome + "x"));
    }

    [Fact]
    public void Unsubscribing_removes_one_address_or_all_of_a_device()
    {
        using var s = new PushService(Dir());
        s.Subscribe("dev1", Chrome);
        s.Subscribe("dev1", Apple);
        s.Subscribe("dev2", Firefox);
        s.Unsubscribe("dev1", Chrome);
        Assert.Equal(2, s.Count);
        s.Unsubscribe("dev1");
        Assert.Equal(1, s.Count);
        Assert.False(s.IsSubscribed("dev1"));
    }

    [Fact]
    public async Task A_push_is_an_empty_signed_post_with_a_time_limit_and_no_text()
    {
        var fake = new FakePushServer();
        using var s = new PushService(Dir(), fake);
        s.Subscribe("dev1", Chrome);
        Assert.Equal(1, await s.NotifyAsync());
        var r = fake.Requests.Single();
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal(Chrome, r.RequestUri!.ToString());
        Assert.Equal(0, fake.BodyLengths.Single());
        Assert.StartsWith("vapid t=", r.Headers.GetValues("Authorization").Single());
        Assert.Equal("3600", r.Headers.GetValues("TTL").Single());
    }

    [Fact]
    public async Task A_push_with_title_and_body_is_encrypted_when_keys_present()
    {
        var fake = new FakePushServer();
        using var s = new PushService(Dir(), fake);
        // Valid p256dh (65-byte uncompressed point) and auth (16-byte secret) base64url strings
        string p256dh = "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U";
        string auth = "140e0gT76y16gT76y16gTw";
        s.Subscribe("dev1", Chrome, p256dh, auth);
        Assert.Equal(1, await s.NotifyAsync("Levity · MyProject", "Answer completed: Fixed the notification issue"));
        var r = fake.Requests.Single();
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal(Chrome, r.RequestUri!.ToString());
        Assert.True(fake.BodyLengths.Single() > 0);
        Assert.Equal("aesgcm", r.Content!.Headers.ContentEncoding.Single());
    }

    [Fact]
    public async Task A_subscription_the_service_says_is_gone_is_forgotten_but_a_hiccup_is_not()
    {
        var fake = new FakePushServer { Reply = u => u.AbsoluteUri.Contains("gone") ? HttpStatusCode.Gone : u.AbsoluteUri.Contains("busy") ? HttpStatusCode.TooManyRequests : HttpStatusCode.Created };
        using var s = new PushService(Dir(), fake);
        s.Subscribe("d", "https://fcm.googleapis.com/gone");
        s.Subscribe("d", "https://fcm.googleapis.com/busy");
        s.Subscribe("d", "https://fcm.googleapis.com/fine");
        Assert.Equal(1, await s.NotifyAsync());
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public async Task A_network_failure_is_reported_as_failed_not_thrown()
    {
        using var s = new PushService(Dir(), new ThrowingHandler());
        Assert.Equal(PushOutcome.Failed, await s.SendAsync(Chrome));
        Assert.Equal(PushOutcome.Rejected, await s.SendAsync("https://evil.example.com/x"));
    }

    [Fact]
    public async Task Quiet_notify_skips_when_a_phone_is_connected_and_spaces_pushes_out()
    {
        var fake = new FakePushServer();
        var clock = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        using var s = new PushService(Dir(), fake, () => clock);
        s.Subscribe("d", Chrome);
        Assert.Equal(0, await s.NotifyIfQuietAsync(1));
        Assert.Empty(fake.Requests);
        Assert.Equal(1, await s.NotifyIfQuietAsync(0));
        Assert.Equal(0, await s.NotifyIfQuietAsync(0));   // same instant: too soon
        clock = clock.AddSeconds(30);
        Assert.Equal(1, await s.NotifyIfQuietAsync(0));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task With_nobody_subscribed_nothing_is_sent()
    {
        var fake = new FakePushServer();
        using var s = new PushService(Dir(), fake);
        Assert.Equal(0, await s.NotifyIfQuietAsync(0));
        Assert.Empty(fake.Requests);
    }
}
