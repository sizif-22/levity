using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services
{
    /// <summary>
    /// Connection helper for the relay. Normal DNS is tried first; if the PC's resolver cannot find the name
    /// (a brand-new workers.dev address can stay "not found" in a router cache for hours), the name is looked up
    /// over HTTPS at Cloudflare instead. TLS still validates the certificate against the real host name.
    /// </summary>
    public static class RelayNet
    {
        private static readonly HttpClient Doh = new() { Timeout = TimeSpan.FromSeconds(6) };

        /// <summary>Shared so connections to the relay reuse one handler.</summary>
        public static readonly HttpMessageInvoker Invoker = new(new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });

        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            string host = context.DnsEndPoint.Host;
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException)
            {
                addresses = await ResolveOverHttpsAsync(host, ct);
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public static async Task<IPAddress[]> ResolveOverHttpsAsync(string host, CancellationToken ct)
        {
            var found = new System.Collections.Generic.List<IPAddress>();

            // A first (IPv4 connects everywhere), then AAAA.
            foreach (int type in new[] { 1, 28 })
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type={type}");
                    request.Headers.Accept.ParseAdd("application/dns-json");

                    using var response = await Doh.SendAsync(request, ct);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

                    if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;
                    foreach (var answer in answers.EnumerateArray())
                    {
                        if (answer.GetProperty("type").GetInt32() == type
                            && IPAddress.TryParse(answer.GetProperty("data").GetString(), out var ip))
                        {
                            found.Add(ip);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* try the next record type */ }
            }

            if (found.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
            return found.ToArray();
        }
    }
}
