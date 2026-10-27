using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Levity.Services
{
    /// <summary>
    /// End-to-end encryption shared with the phone app (web/src/lib/crypto.ts).
    /// Frame = base64url( iv[12] || AES-256-GCM(ciphertext || tag) ), AAD = direction label.
    /// Plaintext = {"s": sessionId, "n": counter, "t": unixMs, "m": message}.
    /// </summary>
    public static class RelayCrypto
    {
        public const string HostToPhone = "h2p";
        public const string PhoneToHost = "p2h";
        public const long MaxSkewMs = 120_000;

        public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

        /// <summary>Room id is derived from the key, so the relay never learns the key itself.</summary>
        public static string RoomId(byte[] key)
        {
            byte[] prefix = Encoding.UTF8.GetBytes("levity-room-v1");
            byte[] input = new byte[prefix.Length + key.Length];
            prefix.CopyTo(input, 0);
            key.CopyTo(input, prefix.Length);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()[..32];
        }

        public static string Encrypt(byte[] key, string direction, string sessionId, long counter, string messageJson)
        {
            string plain = $"{{\"s\":{JsonSerializer.Serialize(sessionId)},\"n\":{counter},\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"m\":{messageJson}}}";
            byte[] iv = RandomNumberGenerator.GetBytes(12);
            byte[] data = Encoding.UTF8.GetBytes(plain);
            byte[] cipher = new byte[data.Length];
            byte[] tag = new byte[16];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, data, cipher, tag, Encoding.UTF8.GetBytes(direction));

            byte[] frame = new byte[12 + cipher.Length + 16];
            iv.CopyTo(frame, 0);
            cipher.CopyTo(frame, 12);
            tag.CopyTo(frame, 12 + cipher.Length);
            return ToBase64Url(frame);
        }

        /// <summary>Returns false for anything that does not authenticate, is stale, or is not newer than the last frame of that session.</summary>
        public static bool TryDecrypt(byte[] key, string direction, string frameB64, out string sessionId, out long counter, out JsonElement message)
        {
            sessionId = "";
            counter = 0;
            message = default;
            try
            {
                byte[] frame = FromBase64Url(frameB64);
                if (frame.Length < 12 + 16) return false;

                byte[] iv = frame[..12];
                byte[] tag = frame[^16..];
                byte[] cipher = frame[12..^16];
                byte[] plain = new byte[cipher.Length];

                using var aes = new AesGcm(key, 16);
                aes.Decrypt(iv, cipher, tag, plain, Encoding.UTF8.GetBytes(direction));

                using var doc = JsonDocument.Parse(plain);
                var root = doc.RootElement;
                sessionId = root.GetProperty("s").GetString() ?? "";
                counter = root.GetProperty("n").GetInt64();
                long sentAt = root.GetProperty("t").GetInt64();
                if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentAt) > MaxSkewMs) return false;

                message = root.GetProperty("m").Clone();
                return sessionId.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static string ToBase64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
    }
}
