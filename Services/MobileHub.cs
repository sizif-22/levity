using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
{
    public record PairRequest(string Id, string Name, string Code, DateTime ReceivedUtc);

    /// <summary>
    /// Owns every relay connection: one per paired phone (each with its own key), plus a short-lived
    /// pairing connection that exists only while the user has a pairing QR open.
    ///
    /// Auth model: a phone is trusted only after the user approves it on this PC. Approval hands the
    /// phone a fresh random key that only that phone and this PC hold; the pairing QR key is single-use
    /// and expires after <see cref="PairingWindow"/>.
    /// </summary>
    public sealed class MobileHub : IDisposable
    {
        public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(5);
        public const int MaxDevices = 8;
        private const int MaxPendingRequests = 3;

        private readonly AppSettings _settings;
        private readonly object _lock = new();
        private readonly Dictionary<string, RelayClient> _clients = new();   // deviceId -> connection
        private readonly Dictionary<string, PairRequest> _pending = new();   // phone connection id -> request
        private RelayClient? _pairing;
        private CancellationTokenSource? _pairingTimer;

        public event Action<string, JsonElement>? OnMessage;    // ("deviceId/phoneId", message)
        public event Action<PairRequest>? OnPairRequest;
        public event Action? OnChanged;

        public bool IsRunning { get; private set; }
        public DateTime? PairingExpiresUtc { get; private set; }
        public string? PairingKey { get; private set; }          // base64url, shown in the QR only
        public IReadOnlyList<PairRequest> PendingRequests { get { lock (_lock) return _pending.Values.ToList(); } }

        public MobileHub(AppSettings settings) { _settings = settings; }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_settings.RelayUrl)) return;
            IsRunning = true;
            foreach (var d in _settings.Devices) ConnectDevice(d);
            OnChanged?.Invoke();
        }

        public void Stop()
        {
            IsRunning = false;
            EndPairing();
            lock (_lock)
            {
                foreach (var c in _clients.Values) c.Stop();
                _clients.Clear();
            }
            OnChanged?.Invoke();
        }

        // ---- Devices ----------------------------------------------------------------------

        public IEnumerable<(PairedDevice Device, bool Connected, int Phones)> DeviceStates()
        {
            foreach (var d in _settings.Devices.ToList())
            {
                RelayClient? c;
                lock (_lock) _clients.TryGetValue(d.Id, out c);
                yield return (d, c?.IsConnected == true, c?.ClientCount ?? 0);
            }
        }

        public void Revoke(string deviceId)
        {
            lock (_lock)
            {
                if (_clients.Remove(deviceId, out var c)) c.Stop();   // closes the room: the phone is cut off immediately
            }
            _settings.Devices.RemoveAll(d => d.Id == deviceId);
            _settings.Save();
            OnChanged?.Invoke();
        }

        private void ConnectDevice(PairedDevice device)
        {
            byte[]? key = device.TryGetKey();
            if (key == null || string.IsNullOrWhiteSpace(_settings.RelayUrl)) return;

            var client = new RelayClient();
            client.OnStateChanged += () => OnChanged?.Invoke();
            client.OnMessage += (phone, msg) =>
            {
                device.LastSeenUtc = DateTime.UtcNow;
                OnMessage?.Invoke($"{device.Id}/{phone}", msg);
            };
            lock (_lock)
            {
                if (_clients.Remove(device.Id, out var old)) old.Stop();
                _clients[device.Id] = client;
            }
            client.Start(_settings.RelayUrl!, key);
        }

        // ---- Messaging --------------------------------------------------------------------

        public Task BroadcastAsync(string messageJson)
        {
            List<RelayClient> targets;
            lock (_lock) targets = _clients.Values.ToList();
            return Task.WhenAll(targets.Select(c => c.BroadcastAsync(messageJson)));
        }

        public Task SendAsync(string clientId, string messageJson)
        {
            int slash = clientId.IndexOf('/');
            if (slash < 0) return Task.CompletedTask;
            RelayClient? c;
            lock (_lock) _clients.TryGetValue(clientId[..slash], out c);
            return c?.SendAsync(clientId[(slash + 1)..], messageJson) ?? Task.CompletedTask;
        }

        public int ConnectedPhones { get { lock (_lock) return _clients.Values.Sum(c => c.ClientCount); } }

        // ---- Pairing ----------------------------------------------------------------------

        /// <summary>Opens a pairing window and returns the one-time key that goes in the QR (never stored).</summary>
        public string BeginPairing()
        {
            if (string.IsNullOrWhiteSpace(_settings.RelayUrl)) throw new InvalidOperationException("Relay URL is not configured.");
            if (_settings.Devices.Count >= MaxDevices) throw new InvalidOperationException($"Limit of {MaxDevices} devices reached. Revoke one first.");

            EndPairing();
            byte[] key = RelayCrypto.NewKey();
            PairingKey = RelayCrypto.ToBase64Url(key);
            PairingExpiresUtc = DateTime.UtcNow + PairingWindow;

            var client = new RelayClient();
            client.OnMessage += (phone, msg) => HandlePairingMessage(phone, msg);
            client.OnStateChanged += () => OnChanged?.Invoke();
            _pairing = client;
            client.Start(_settings.RelayUrl!, key);

            var timer = new CancellationTokenSource();
            _pairingTimer = timer;
            _ = Task.Delay(PairingWindow, timer.Token).ContinueWith(t => { if (!t.IsCanceled) EndPairing(); });

            OnChanged?.Invoke();
            return PairingKey;
        }

        public void EndPairing()
        {
            try { _pairingTimer?.Cancel(); } catch { }
            _pairingTimer = null;
            _pairing?.Stop();
            _pairing = null;
            PairingKey = null;
            PairingExpiresUtc = null;
            lock (_lock) _pending.Clear();
            OnChanged?.Invoke();
        }

        private void HandlePairingMessage(string phone, JsonElement msg)
        {
            if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("type", out var t) || t.GetString() != "pair_request") return;

            string name = Sanitize(msg.TryGetProperty("name", out var n) ? n.GetString() : null, 40);
            string code = msg.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            if (name.Length == 0) name = "Phone";
            if (!Regex.IsMatch(code, "^[0-9]{4}$")) return;

            PairRequest request;
            lock (_lock)
            {
                if (_pending.Count >= MaxPendingRequests && !_pending.ContainsKey(phone)) return;
                request = new PairRequest(phone, name, code, DateTime.UtcNow);
                _pending[phone] = request;
            }
            OnPairRequest?.Invoke(request);
            OnChanged?.Invoke();
        }

        public async Task ApproveAsync(string requestId)
        {
            PairRequest? request;
            lock (_lock) _pending.Remove(requestId, out request);
            var pairing = _pairing;
            if (request == null || pairing == null || _settings.Devices.Count >= MaxDevices) return;

            byte[] deviceKey = RelayCrypto.NewKey();
            var device = PairedDevice.Create(request.Name, deviceKey);
            _settings.Devices.Add(device);
            _settings.Save();

            string reply = JsonSerializer.Serialize(new
            {
                type = "pair_approved",
                deviceId = device.Id,
                key = RelayCrypto.ToBase64Url(deviceKey)
            });
            await pairing.SendAsync(requestId, reply);
            await Task.Delay(600);     // let the frame flush before the one-time channel closes

            EndPairing();              // the pairing key is single-use
            if (IsRunning) ConnectDevice(device);
            OnChanged?.Invoke();
        }

        public async Task DenyAsync(string requestId)
        {
            lock (_lock) _pending.Remove(requestId);
            if (_pairing != null) await _pairing.SendAsync(requestId, "{\"type\":\"pair_denied\"}");
            OnChanged?.Invoke();
        }

        private static string Sanitize(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (char ch in s.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.' or '(' or ')') sb.Append(ch);
                if (sb.Length >= max) break;
            }
            return sb.ToString();
        }

        public void Dispose() => Stop();
    }
}
