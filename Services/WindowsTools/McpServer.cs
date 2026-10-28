using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services.WindowsTools
{
    /// <summary>
    /// The Windows toolbox as an MCP server (the way the assistant loads extra tools), living inside Levity, so there is no second program
    /// to run or keep in memory. It listens on 127.0.0.1 only, needs a secret bearer token, refuses anything that comes from a web page
    /// (an Origin header) or with a foreign Host header, and caps sizes and time. It is only started when the user turns the toolbox on.
    /// </summary>
    public sealed class McpServer : IDisposable
    {
        public static readonly string[] SupportedProtocols = { "2025-06-18", "2025-03-26", "2024-11-05" };
        public const int MaxBodyBytes = 1_000_000;
        public const int MaxHeaderBytes = 16_384;
        public const int MaxReplyChars = 60_000;

        private readonly ToolRegistry _registry;
        private readonly ChangeJournal _journal;
        private readonly byte[] _token;
        private TcpListener? _listener;
        private CancellationTokenSource? _stop;

        public int Port { get; private set; }
        public bool IsRunning => _listener != null;

        public McpServer(ToolRegistry registry, ChangeJournal journal, string token)
        {
            _registry = registry;
            _journal = journal;
            _token = Encoding.UTF8.GetBytes(token);
        }

        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        /// <summary>Starts listening on the loopback address. Tries the preferred port (so the assistant's saved address stays valid) and falls back to any free one.</summary>
        public bool Start(int preferredPort = 0)
        {
            if (_listener != null) return true;
            foreach (int port in preferredPort > 0 ? new[] { preferredPort, 0 } : new[] { 0 })
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, port);
                    l.Start();
                    _listener = l;
                    Port = ((IPEndPoint)l.LocalEndpoint).Port;
                    _stop = new CancellationTokenSource();
                    _ = Task.Run(() => AcceptLoop(l, _stop.Token));
                    return true;
                }
                catch (SocketException) { /* port taken: try the next */ }
            }
            return false;
        }

        public void Stop()
        {
            try { _stop?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        public void Dispose() => Stop();

        // ---- sockets ----

        private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop); }
                catch { return; }
                _ = Task.Run(() => Serve(client, stop));
            }
        }

        private async Task Serve(TcpClient client, CancellationToken stop)
        {
            using (client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var stream = client.GetStream();
                    var request = await ReadRequest(stream, timeout.Token);
                    (int status, string body) = request == null
                        ? (400, "{\"error\":\"bad request\"}")
                        : await HandleHttpAsync(request.Method, request.Path, request.Headers, request.Body, timeout.Token);
                    await Write(stream, status, body, timeout.Token);
                }
                catch { /* a broken connection is not our problem */ }
            }
        }

        private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

        private static async Task<HttpRequest?> ReadRequest(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            var head = new MemoryStream();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) return null;
                head.Write(buffer, 0, n);
                if (head.Length > MaxHeaderBytes + MaxBodyBytes) return null;
                headerEnd = IndexOf(head.GetBuffer(), (int)head.Length, "\r\n\r\n"u8);
                if (headerEnd < 0 && head.Length > MaxHeaderBytes) return null;
            }
            if (headerEnd > MaxHeaderBytes) return null;
            string headText = Encoding.ASCII.GetString(head.GetBuffer(), 0, headerEnd);
            string[] lines = headText.Split("\r\n");
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            int length = 0;
            if (headers.TryGetValue("Content-Length", out string? cl) && (!int.TryParse(cl, out length) || length < 0 || length > MaxBodyBytes)) return null;
            if (headers.ContainsKey("Transfer-Encoding")) return null;   // chunked bodies are not accepted: keep the parser small
            var body = new MemoryStream();
            int already = (int)head.Length - (headerEnd + 4);
            if (already > 0) body.Write(head.GetBuffer(), headerEnd + 4, Math.Min(already, length));
            while (body.Length < length)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) return null;
                body.Write(buffer, 0, Math.Min(n, length - (int)body.Length));
            }
            return new HttpRequest(first[0], first[1], headers, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
        }

        private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
        {
            for (int i = 0; i + pattern.Length <= length; i++)
                if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
            return -1;
        }

        private static async Task Write(NetworkStream stream, int status, string body, CancellationToken ct)
        {
            string reason = status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
        }

        // ---- HTTP rules (testable without sockets) ----

        public async Task<(int Status, string Body)> HandleHttpAsync(string method, string path, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default)
        {
            string Header(string name) => headers.TryGetValue(name, out var v) ? v : "";

            // a web page must never be able to drive this, even with the token in hand
            if (headers.ContainsKey("Origin")) return (403, "{\"error\":\"browser requests are not allowed\"}");
            string host = Header("Host");
            if (host.Length > 0 && !(host == $"127.0.0.1:{Port}" || host == $"localhost:{Port}" || host == "127.0.0.1" || host == "localhost"))
                return (403, "{\"error\":\"unexpected host\"}");

            string auth = Header("Authorization");
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth[7..].Trim()), _token))
                return (401, "{\"error\":\"unauthorized\"}");

            string cleanPath = path.Split('?')[0];
            if (cleanPath != "/mcp") return (404, "{\"error\":\"not found\"}");
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return (405, "{\"error\":\"POST only\"}");

            string? reply = await HandleRpcAsync(body, ct);
            return reply == null ? (202, "") : (200, reply);
        }

        // ---- JSON-RPC ----

        /// <summary>Handles one JSON-RPC message or batch. Returns null when it was only notifications (nothing to answer).</summary>
        public async Task<string?> HandleRpcAsync(string body, CancellationToken ct = default)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch { return Error(null, -32700, "Parse error"); }

            if (node is JsonArray batch)
            {
                var replies = new JsonArray();
                foreach (var item in batch)
                {
                    var r = await HandleOne(item as JsonObject, ct);
                    if (r != null) replies.Add(r);
                }
                return replies.Count == 0 ? null : replies.ToJsonString();
            }
            var single = await HandleOne(node as JsonObject, ct);
            return single?.ToJsonString();
        }

        private async Task<JsonObject?> HandleOne(JsonObject? msg, CancellationToken ct)
        {
            if (msg == null) return ErrorObject(null, -32600, "Invalid request");
            JsonNode? id = msg["id"]?.DeepClone();
            string? method = (string?)msg["method"];
            if (method == null) return id == null ? null : ErrorObject(id, -32600, "Invalid request");
            bool isNotification = id == null;

            switch (method)
            {
                case "initialize":
                {
                    string requested = (string?)msg["params"]?["protocolVersion"] ?? "";
                    string version = Array.IndexOf(SupportedProtocols, requested) >= 0 ? requested : SupportedProtocols[0];
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "levity-windows", ["version"] = "1.0" },
                        ["instructions"] = "Typed tools for reading and changing Windows settings. Every change is recorded in Levity's change journal and can be reverted. Prefer these over writing PowerShell.",
                    });
                }
                case "ping": return isNotification ? null : Result(id, new JsonObject());
                case "tools/list": return isNotification ? null : Result(id, new JsonObject { ["tools"] = _registry.Definitions() });
                case "tools/call":
                {
                    if (isNotification) return null;
                    string? name = (string?)msg["params"]?["name"];
                    var args = msg["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    if (string.IsNullOrWhiteSpace(name)) return ErrorObject(id, -32602, "Missing tool name");
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(TimeSpan.FromSeconds(60));
                    var ctx = new ToolContext { Journal = _journal, DryRun = (bool?)args["dry_run"] ?? false, Cancel = limit.Token };
                    var result = await _registry.CallAsync(name, args, ctx);
                    return Result(id, ToCallResult(result));
                }
                default:
                    if (method.StartsWith("notifications/", StringComparison.Ordinal) || isNotification) return null;
                    return ErrorObject(id, -32601, "Method not found");
            }
        }

        private static JsonObject ToCallResult(ToolResult r)
        {
            string text = r.Text;
            if (r.Data != null) text += "\n" + r.Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (text.Length > MaxReplyChars) text = text[..MaxReplyChars] + "\n... (cut: too long; narrow the request)";
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = !r.Ok,
            };
        }

        private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        private static JsonObject ErrorObject(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
        private static string Error(JsonNode? id, int code, string message) => ErrorObject(id, code, message).ToJsonString();
    }
}
