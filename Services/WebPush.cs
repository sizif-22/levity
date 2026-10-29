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

namespace Levity.Services
{
    public enum PushOutcome { Sent, Gone, Failed, Rejected }

    public sealed record PushSubscription(string Device, string Endpoint, DateTime AddedUtc);

    /// <summary>
    /// Web Push without a payload. The phone's service worker shows a generic "Levity has news" and the app then fetches the details through the
    /// encrypted channel, so nothing readable ever reaches a push service. Signed with a VAPID key pair that lives only on this PC.
    /// </summary>
    public static class WebPushRules
    {
        public const int MaxPerDevice = 3, MaxTotal = 10, MaxEndpointLength = 2048;

        private static readonly string[] AllowedHostSuffixes =
        {
            "fcm.googleapis.com", "android.googleapis.com",                       // Chrome, Edge, Android
            "push.services.mozilla.com",                                          // Firefox
            "push.apple.com",                                                     // Safari, iPhone, iPad
            "notify.windows.com",                                                 // Windows (Edge legacy)
        };

        /// <summary>The phone tells the PC where to send pushes, so the address is checked: https, a known push service, no credentials, no odd ports.</summary>
        public static bool IsAllowedEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > MaxEndpointLength) return false;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !u.IsDefaultPort) return false;
            string h = u.IdnHost.ToLowerInvariant();
            return AllowedHostSuffixes.Any(s => h == s || h.EndsWith("." + s, StringComparison.Ordinal));
        }

        /// <summary>Push only when nobody is looking at the app (a phone connected already shows the news), and not more than once in a short while.</summary>
        public static bool ShouldNotify(bool enabled, int connectedPhones, DateTime? lastSentUtc, DateTime nowUtc, TimeSpan? minGap = null)
        {
            if (!enabled || connectedPhones > 0) return false;
            return lastSentUtc == null || nowUtc - lastSentUtc.Value >= (minGap ?? TimeSpan.FromSeconds(20));
        }

        public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
    }

    public sealed class VapidKeys
    {
        public string PublicKey { get; }          // 65-byte uncompressed point, base64url: what the browser needs to subscribe
        private readonly ECDsa _key;

        private VapidKeys(ECDsa key)
        {
            _key = key;
            var q = key.ExportParameters(false).Q;
            PublicKey = WebPushRules.Base64Url(new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray());
        }

        public static VapidKeys Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        public static VapidKeys FromPrivate(byte[] pkcs8) { var k = ECDsa.Create(); k.ImportPkcs8PrivateKey(pkcs8, out _); return new VapidKeys(k); }
        public byte[] ExportPrivate() => _key.ExportPkcs8PrivateKey();

        /// <summary>The Authorization header value for a push to <paramref name="endpoint"/> (RFC 8292).</summary>
        public string AuthorizationFor(string endpoint, string subject, DateTime nowUtc)
        {
            var u = new Uri(endpoint);
            string aud = u.GetLeftPart(UriPartial.Authority);
            long exp = new DateTimeOffset(nowUtc).ToUnixTimeSeconds() + 12 * 3600;
            string header = WebPushRules.Base64Url(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
            string claims = WebPushRules.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud, exp, sub = subject }));
            string signing = header + "." + claims;
            byte[] sig = _key.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"vapid t={signing}.{WebPushRules.Base64Url(sig)}, k={PublicKey}";
        }
    }

    /// <summary>Keys and subscriptions in one small file, plus the sending. The HTTP handler is injectable so tests never touch the network.</summary>
    public sealed class PushService : IDisposable
    {
        public const string DefaultSubject = "mailto:levity-push@example.com";

        private readonly string _file;
        private readonly HttpClient _http;
        private readonly Func<DateTime> _now;
        private readonly object _lock = new();
        private VapidKeys _keys = null!;
        private readonly List<PushSubscription> _subs = new();
        private DateTime? _lastSent;

        public string Subject { get; set; } = DefaultSubject;

        public PushService(string dataDir, HttpMessageHandler? handler = null, Func<DateTime>? now = null)
        {
            Directory.CreateDirectory(dataDir);
            _file = Path.Combine(dataDir, "push.json");
            _http = handler == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
            _now = now ?? (() => DateTime.UtcNow);
            Load();
        }

        public string PublicKey => _keys.PublicKey;
        public int Count { get { lock (_lock) return _subs.Count; } }
        public bool HasSubscribers => Count > 0;
        public bool IsSubscribed(string device) { lock (_lock) return _subs.Any(s => s.Device == device); }

        private sealed class Disk { public string? PrivateKey { get; set; } public List<PushSubscription> Subs { get; set; } = new(); }

        private void Load()
        {
            try
            {
                if (File.Exists(_file))
                {
                    var d = JsonSerializer.Deserialize<Disk>(File.ReadAllText(_file));
                    if (d?.PrivateKey != null) { _keys = VapidKeys.FromPrivate(WebPushRules.FromBase64Url(d.PrivateKey)); _subs.AddRange(d.Subs.Where(s => WebPushRules.IsAllowedEndpoint(s.Endpoint))); return; }
                }
            }
            catch { }
            _keys = VapidKeys.Generate();   // first run, or an unreadable file: a new key means old subscriptions are void, so the phone subscribes again
            Save();
        }

        private void Save()
        {
            try
            {
                string tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Disk { PrivateKey = WebPushRules.Base64Url(_keys.ExportPrivate()), Subs = _subs }));
                File.Move(tmp, _file, true);
            }
            catch { }
        }

        /// <summary>Remembers where to push for this device. Returns an error text, or null when stored.</summary>
        public string? Subscribe(string device, string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(device)) return "Unknown device.";
            if (!WebPushRules.IsAllowedEndpoint(endpoint)) return "That push address is not one Levity trusts.";
            lock (_lock)
            {
                _subs.RemoveAll(s => s.Endpoint == endpoint);
                var mine = _subs.Where(s => s.Device == device).ToList();
                while (mine.Count >= WebPushRules.MaxPerDevice) { _subs.Remove(mine[0]); mine.RemoveAt(0); }
                if (_subs.Count >= WebPushRules.MaxTotal) return "Too many phones are subscribed. Turn it off on one first.";
                _subs.Add(new PushSubscription(device, endpoint!, _now()));
                Save();
            }
            return null;
        }

        public void Unsubscribe(string device, string? endpoint = null)
        {
            lock (_lock) { if (_subs.RemoveAll(s => s.Device == device && (endpoint == null || s.Endpoint == endpoint)) > 0) Save(); }
        }

        /// <summary>Sends one empty push to every subscription of every device. Dead subscriptions (404, 410) are dropped. Returns how many were delivered to the push service.</summary>
        public async Task<int> NotifyAsync(CancellationToken ct = default)
        {
            List<PushSubscription> targets;
            lock (_lock) targets = _subs.ToList();
            int sent = 0;
            foreach (var s in targets)
            {
                var outcome = await SendAsync(s.Endpoint, ct);
                if (outcome == PushOutcome.Sent) sent++;
                else if (outcome == PushOutcome.Gone) { lock (_lock) { _subs.Remove(s); Save(); } }
            }
            return sent;
        }

        /// <summary>Applies the quiet rules, then pushes. Called from the app's events.</summary>
        public async Task<int> NotifyIfQuietAsync(int connectedPhones, CancellationToken ct = default)
        {
            DateTime now = _now();
            lock (_lock) { if (!WebPushRules.ShouldNotify(_subs.Count > 0, connectedPhones, _lastSent, now)) return 0; _lastSent = now; }
            return await NotifyAsync(ct);
        }

        public async Task<PushOutcome> SendAsync(string endpoint, CancellationToken ct = default)
        {
            if (!WebPushRules.IsAllowedEndpoint(endpoint)) return PushOutcome.Rejected;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                req.Headers.TryAddWithoutValidation("Authorization", _keys.AuthorizationFor(endpoint, Subject, _now()));
                req.Headers.TryAddWithoutValidation("TTL", "3600");
                req.Headers.TryAddWithoutValidation("Urgency", "normal");
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return PushOutcome.Sent;
                return res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ? PushOutcome.Gone : PushOutcome.Failed;
            }
            catch { return PushOutcome.Failed; }
        }

        public void Dispose() => _http.Dispose();
    }
}
