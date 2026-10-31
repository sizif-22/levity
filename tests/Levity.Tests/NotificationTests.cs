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

public class WebPushCryptoTests
{
    private static byte[] B(string s) => WebPushRules.FromBase64Url(s);

    // The worked example in RFC 8291 appendix A.
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Expected = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void It_reproduces_the_example_of_RFC_8291_exactly()
    {
        byte[] pub = B(AsPublic);
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = B(AsPrivate),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
        byte[] body = WebPushCrypto.Encrypt(Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"), B(UaPublic), B(AuthSecret), sender, B(Salt));
        Assert.Equal(Expected, WebPushRules.Base64Url(body));
    }

    [Fact]
    public void Each_message_gets_a_new_salt_and_sender_key_and_a_well_formed_header()
    {
        using var phone = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = phone.ExportParameters(false).Q;
        byte[] ua = new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
        byte[] auth = RandomNumberGenerator.GetBytes(16);

        byte[] a = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes("hello"), ua, auth);
        byte[] b = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes("hello"), ua, auth);
        Assert.NotEqual(a[..16], b[..16]);                          // salt
        Assert.Equal(new byte[] { 0, 0, 0x10, 0 }, a[16..20]);      // record size 4096
        Assert.Equal(65, a[20]);                                    // key id length
        Assert.Equal(4, a[21]);                                     // key id is an uncompressed point
        Assert.Equal(86 + "hello".Length + 1 + 16, a.Length);       // header + message + delimiter + tag
    }

    [Fact]
    public void Bad_keys_and_too_long_messages_are_refused()
    {
        byte[] ua = B(UaPublic), auth = B(AuthSecret);
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(new byte[1], ua[..64], auth));
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(new byte[1], ua, auth[..15]));
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintext + 1], ua, auth));
        Assert.NotEmpty(WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintext], ua, auth));
    }
}

public class PushPayloadTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(Uri Uri, string[] Encoding, byte[] Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!, request.Content!.Headers.ContentEncoding.ToArray(), await request.Content.ReadAsByteArrayAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private const string Apple = "https://web.push.apple.com/QWERTY";
    private static string Dir() => Path.Combine(Path.GetTempPath(), "push-" + Guid.NewGuid().ToString("N"));

    /// <summary>The phone's side of RFC 8291, written separately from the sender: what a phone would do with the bytes it receives.</summary>
    private static string Decrypt(byte[] body, ECDiffieHellman phone, byte[] ua, byte[] auth)
    {
        byte[] salt = body[..16];
        int idLen = body[20];
        byte[] asPublic = body[21..(21 + idLen)];
        byte[] data = body[(21 + idLen)..];
        using var sender = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        byte[] secret = phone.DeriveRawSecretAgreement(sender.PublicKey);
        byte[] ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, auth, Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), ua, asPublic));
        byte[] cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        byte[] plain = new byte[data.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, data[..^16], data[^16..], plain);
        Assert.Equal(2, plain[^1]);   // the last-record delimiter
        return Encoding.UTF8.GetString(plain[..^1]);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static (ECDiffieHellman Phone, string P256dh, string Auth, byte[] Ua, byte[] AuthBytes) NewPhone()
    {
        var phone = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = phone.ExportParameters(false).Q;
        byte[] ua = new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
        byte[] auth = RandomNumberGenerator.GetBytes(16);
        return (phone, WebPushRules.Base64Url(ua), WebPushRules.Base64Url(auth), ua, auth);
    }

    [Fact]
    public async Task The_phone_can_read_the_title_body_and_tag_of_a_push()
    {
        var cap = new Capture();
        using var s = new PushService(Dir(), cap);
        var (phone, p256dh, auth, ua, authBytes) = NewPhone();
        using (phone)
        {
            s.Subscribe("dev1", Apple, p256dh, auth);
            Assert.Equal(1, await s.NotifyAsync("Levity · PostPilot · done", "Re: add TikTok images\nPlan written.\n44 steps · 11 min 02 s", default, "levity-postpilot"));

            var call = cap.Calls.Single();
            Assert.Equal(new[] { "aes128gcm" }, call.Encoding);
            using var json = JsonDocument.Parse(Decrypt(call.Body, phone, ua, authBytes));
            Assert.Equal("Levity · PostPilot · done", json.RootElement.GetProperty("title").GetString());
            Assert.Equal("Re: add TikTok images\nPlan written.\n44 steps · 11 min 02 s", json.RootElement.GetProperty("body").GetString());
            Assert.Equal("levity-postpilot", json.RootElement.GetProperty("tag").GetString());
        }
    }

    [Fact]
    public void A_device_can_receive_text_only_when_its_subscription_has_keys()
    {
        using var s = new PushService(Dir(), new Capture());
        var (phone, p256dh, auth, _, _) = NewPhone();
        using (phone)
        {
            Assert.False(s.IsDetailed("dev1"));                                   // not subscribed at all
            s.Subscribe("dev1", Apple);
            Assert.True(s.IsSubscribed("dev1"));
            Assert.False(s.IsDetailed("dev1"));                                   // an old subscription: no keys, only an empty ping is possible
            s.Subscribe("dev1", Apple + "2", p256dh, auth);
            Assert.True(s.IsDetailed("dev1"));                                    // renewed with keys
            Assert.False(s.IsDetailed("other-phone"));
        }
    }
}

public class NotificationTextTests
{
    [Fact]
    public void Markdown_marks_and_links_are_removed_and_long_text_is_cut_at_a_word()
    {
        Assert.Equal("Done Added publish_post.ts and the plan", NotificationText.Snippet("## Done\n**Added** `publish_post.ts` and the _plan_", 100));
        Assert.Equal("See the plan", NotificationText.Snippet("See [the plan](file:///C:/x/plan.md)", 100));
        string cut = NotificationText.Snippet("one two three four five six seven eight nine ten", 20);
        Assert.True(cut.Length <= 20, cut);
        Assert.EndsWith("…", cut);
        Assert.DoesNotContain("  ", cut);
        Assert.Equal("", NotificationText.Snippet("   ", 20));
        Assert.Equal("", NotificationText.Snippet(null, 20));
    }

    [Fact]
    public void A_command_keeps_its_redirects_and_wildcards()
    {
        var (_, body) = NotificationText.ForJob(7, "finished", @"D:\PostPilot", "rg -n tiktok *.ts > out.txt");
        Assert.Equal("Job #7: rg -n tiktok *.ts > out.txt", body);
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(662, "11 min 02 s")]
    [InlineData(3900, "1 h 05 min")]
    [InlineData(-5, "0 s")]
    public void Durations_are_short_and_readable(int seconds, string expected) => Assert.Equal(expected, NotificationText.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(@"D:\PostPilot", "PostPilot")]
    [InlineData(@"D:\PostPilot\", "PostPilot")]
    [InlineData("D:/a/b/", "b")]
    [InlineData(@"D:\", @"D:")]
    [InlineData("", "Home")]
    [InlineData(null, "Home")]
    public void The_folder_name_is_its_last_part(string? cwd, string expected) => Assert.Equal(expected, NotificationText.FolderName(cwd));

    [Fact]
    public void The_tag_is_per_project_and_only_plain_characters()
    {
        Assert.Equal("levity-postpilot", NotificationText.Tag("PostPilot"));
        Assert.Equal("levity-my-app--v2", NotificationText.Tag("My App! v2"));
        Assert.NotEqual(NotificationText.Tag("PostPilot"), NotificationText.Tag("Levity"));
        string t = NotificationText.Tag(new string('x', 200));
        Assert.True(t.Length <= 40);
        Assert.Matches("^[a-z0-9-]+$", t);
        Assert.Matches("^[a-z0-9-]+$", NotificationText.Tag("../../etc \"x\""));
    }

    [Fact]
    public void A_finished_turn_says_the_project_what_was_asked_how_the_answer_starts_and_what_it_cost()
    {
        // The real TikTok chat: 44 tool steps, the conclusion in the last answer step, which links a plan file.
        var s = new TurnSummary("PostPilot",
            "I need you to think of a way to publish images as posts on TikTok because currently we are only able to share videos",
            "I have created a comprehensive implementation plan to enable image post publishing on TikTok.\n\nPlease check the plan artifact: [tiktok_image_posts_plan.md](file:///C:/Users/x/brain/tiktok_image_posts_plan.md).\n\n### Summary of Key Changes",
            44, TimeSpan.FromSeconds(662), 3, 120, 8, false);
        var (title, body) = NotificationText.ForTurn(s);

        Assert.Equal("Levity · PostPilot · done", title);
        string[] lines = body.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Re: I need you to think of a way to publish images", lines[0]);
        Assert.True(lines[0].Length <= 4 + NotificationText.RequestChars, lines[0]);
        Assert.StartsWith("I have created a comprehensive implementation plan", lines[1]);
        Assert.True(lines[1].Length <= NotificationText.AnswerChars, lines[1]);
        Assert.Equal("44 steps · 11 min 02 s · 3 files changed (+120/-8)", lines[2]);
        Assert.DoesNotContain("file:///", body);
        Assert.DoesNotContain("](", body);
    }

    [Fact]
    public void Failures_are_marked_and_missing_parts_are_left_out()
    {
        var (title, body) = NotificationText.ForTurn(new TurnSummary("Levity", null, null, 1, TimeSpan.FromSeconds(3), 1, 0, 0, true));
        Assert.Equal("Levity · Levity · failed", title);
        Assert.Equal("1 step · 3 s · 1 file changed", body);

        var (_, empty) = NotificationText.ForTurn(new TurnSummary("Levity", null, null, 0, TimeSpan.Zero, 0, 0, 0, false));
        Assert.Equal("The assistant finished in Levity.", empty);
        var (_, bad) = NotificationText.ForTurn(new TurnSummary("Levity", "", "", 0, TimeSpan.Zero, 0, 0, 0, true));
        Assert.Equal("The assistant stopped with an error in Levity.", bad);
    }

    [Fact]
    public void A_job_notice_names_the_project_and_the_command()
    {
        var (title, body) = NotificationText.ForJob(3, "failed (exit 1)", @"D:\PostPilot", "npx tsc --noEmit");
        Assert.Equal("Levity · PostPilot · job failed (exit 1)", title);
        Assert.Equal("Job #3: npx tsc --noEmit", body);
        Assert.Equal("Job #4 finished", NotificationText.ForJob(4, "finished", null, "").Body);
    }

    [Fact]
    public void Details_are_on_unless_the_owner_turns_them_off()
    {
        string? old = Environment.GetEnvironmentVariable("LEVITY_PUSH_DETAILS");
        try
        {
            Environment.SetEnvironmentVariable("LEVITY_PUSH_DETAILS", null);
            Assert.True(NotificationText.PushDetailsEnabled);
            Environment.SetEnvironmentVariable("LEVITY_PUSH_DETAILS", "0");
            Assert.False(NotificationText.PushDetailsEnabled);
            Environment.SetEnvironmentVariable("LEVITY_PUSH_DETAILS", "1");
            Assert.True(NotificationText.PushDetailsEnabled);
        }
        finally { Environment.SetEnvironmentVariable("LEVITY_PUSH_DETAILS", old); }
    }
}
