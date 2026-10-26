using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
{
    /// <summary>
    /// Outbound-only connection to the relay. No port is opened on this PC; phones reach it through the
    /// relay, and every frame is end-to-end encrypted with a key only this PC and the paired phones know.
    /// </summary>
    public sealed class RelayClient : IAsyncDisposable
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _clients = new();
        private readonly Dictionary<string, long> _lastCounter = new();
        private readonly string _sessionId = Guid.NewGuid().ToString("N")[..16];
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private byte[] _key = Array.Empty<byte>();
        private long _counter;

        public event Action<string, JsonElement>? OnMessage;   // (phone id, decrypted message)
        public event Action<string>? OnClientJoined;           // phone id
        public event Action? OnStateChanged;

        public bool IsRunning => _cts != null;
        public bool IsConnected => _socket?.State == WebSocketState.Open;
        public int ClientCount { get { lock (_lock) return _clients.Count; } }
        public string LastError { get; private set; } = "";

        public void Start(string relayUrl, byte[] key)
        {
            Stop();
            _key = key;
            _cts = new CancellationTokenSource();
            _ = RunAsync(relayUrl, _cts.Token);
            OnStateChanged?.Invoke();
        }

        public void Stop()
        {
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            lock (_lock) { _clients.Clear(); _lastCounter.Clear(); }
            LastError = "";
            OnStateChanged?.Invoke();
        }

        public Task BroadcastAsync(string messageJson)
        {
            if (ClientCount == 0) return Task.CompletedTask;
            return SendFrameAsync(null, messageJson);
        }

        public Task SendAsync(string clientId, string messageJson) => SendFrameAsync(clientId, messageJson);

        private async Task SendFrameAsync(string? to, string messageJson)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                string frame = RelayCrypto.Encrypt(_key, RelayCrypto.HostToPhone, _sessionId, ++_counter, messageJson);
                string envelope = to == null
                    ? $"{{\"d\":\"{frame}\"}}"
                    : $"{{\"to\":{JsonSerializer.Serialize(to)},\"d\":\"{frame}\"}}";
                await socket.SendAsync(Encoding.UTF8.GetBytes(envelope), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { /* the receive loop notices a dead socket and reconnects */ }
            finally { _sendLock.Release(); }
        }

        private async Task RunAsync(string relayUrl, CancellationToken ct)
        {
            string room = RelayCrypto.RoomId(_key);
            string baseUrl = relayUrl.TrimEnd('/');
            int attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _socket = socket;

                try
                {
                    await socket.ConnectAsync(new Uri($"{baseUrl}/ws?room={room}&role=host"), ct);
                    attempt = 0;
                    LastError = "";
                    OnStateChanged?.Invoke();
                    await ReceiveLoopAsync(socket, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { LastError = ex.Message; }
                finally { socket.Dispose(); }

                lock (_lock) _clients.Clear();
                OnStateChanged?.Invoke();

                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 << Math.Min(attempt++, 4), 30)), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var ms = new System.IO.MemoryStream();

            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 2_000_000) return;
                } while (!result.EndOfMessage);

                HandleEnvelope(ms.ToArray());
            }
        }

        private void HandleEnvelope(byte[] json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string client = root.TryGetProperty("c", out var c) ? c.GetString() ?? "" : "";

                if (root.TryGetProperty("ev", out var ev))
                {
                    switch (ev.GetString())
                    {
                        case "join":
                            lock (_lock) _clients.Add(client);
                            OnStateChanged?.Invoke();
                            OnClientJoined?.Invoke(client);
                            break;
                        case "leave":
                            lock (_lock) _clients.Remove(client);
                            OnStateChanged?.Invoke();
                            break;
                    }
                    return;
                }

                if (client.Length == 0 || !root.TryGetProperty("d", out var d) || d.GetString() is not string frame) return;
                if (!RelayCrypto.TryDecrypt(_key, RelayCrypto.PhoneToHost, frame, out string session, out long counter, out var message)) return;

                // Replay protection: a session's counter must strictly increase.
                lock (_lock)
                {
                    if (_lastCounter.TryGetValue(session, out long last) && counter <= last) return;
                    _lastCounter[session] = counter;
                }

                OnMessage?.Invoke(client, message);
            }
            catch { /* malformed frames are dropped */ }
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            await Task.CompletedTask;
        }
    }
}
