using System;
using System.Linq;
using System.Security.Cryptography;

namespace Levity.Services
{
    /// <summary>
    /// Encrypts a web push message for one phone: RFC 8291 (message encryption) over RFC 8188 (the aes128gcm content coding), the encoding
    /// every push service accepts, Apple's included. Only the phone, which holds the matching private key, can read it; the push service just relays bytes.
    /// </summary>
    public static class WebPushCrypto
    {
        public const int RecordSize = 4096;
        private const int PublicKeyLength = 65, AuthLength = 16, SaltLength = 16, TagLength = 16;

        /// <summary>The whole request body is limited to 4096 bytes by the push services; this leaves room for the header and the tag.</summary>
        public const int MaxPlaintext = 3993;

        /// <summary>
        /// Returns the request body (header and one encrypted record) for <paramref name="plaintext"/>. <paramref name="uaPublic"/> is the phone's
        /// p256dh key (65 bytes, uncompressed) and <paramref name="authSecret"/> its auth secret (16 bytes). The sender key and the salt are
        /// created fresh unless given (tests give them to reproduce the RFC's example).
        /// </summary>
        public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? senderKey = null, byte[]? salt = null)
        {
            if (uaPublic.Length != PublicKeyLength || uaPublic[0] != 4) throw new ArgumentException("The phone's public key must be 65 bytes, uncompressed.", nameof(uaPublic));
            if (authSecret.Length != AuthLength) throw new ArgumentException("The auth secret must be 16 bytes.", nameof(authSecret));
            if (plaintext.Length > MaxPlaintext) throw new ArgumentException("The message is too long for one push.", nameof(plaintext));
            if (salt != null && salt.Length != SaltLength) throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));

            bool ownKey = senderKey == null;
            senderKey ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                using var phone = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
                });
                byte[] asPublic = Uncompressed(senderKey.ExportParameters(false).Q);
                byte[] ecdh = senderKey.DeriveRawSecretAgreement(phone.PublicKey);

                // RFC 8291 section 3.4: mix the shared secret with the phone's auth secret and both public keys.
                byte[] prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdh, authSecret);
                byte[] keyInfo = Concat(Ascii("WebPush: info\0"), uaPublic, asPublic);
                byte[] ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

                // RFC 8188 section 2.2/2.3: the content key and the nonce come from the salt.
                salt ??= RandomNumberGenerator.GetBytes(SaltLength);
                byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
                byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Ascii("Content-Encoding: aes128gcm\0"));
                byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Ascii("Content-Encoding: nonce\0"));

                // One record: the message plus 0x02, the "last record" delimiter.
                byte[] record = new byte[plaintext.Length + 1];
                plaintext.CopyTo(record, 0);
                record[^1] = 2;

                byte[] cipher = new byte[record.Length];
                byte[] tag = new byte[TagLength];
                using (var aes = new AesGcm(cek, TagLength)) aes.Encrypt(nonce, record, cipher, tag);

                // Header: salt, record size (4 bytes), key id length (1 byte), key id (the sender's public key).
                byte[] header = Concat(salt, new byte[] { 0, 0, (byte)(RecordSize >> 8), (byte)(RecordSize & 0xff), PublicKeyLength }, asPublic);
                return Concat(header, cipher, tag);
            }
            finally { if (ownKey) senderKey.Dispose(); }
        }

        private static byte[] Uncompressed(ECPoint q) => Concat(new byte[] { 4 }, q.X!, q.Y!);
        private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

        private static byte[] Concat(params byte[][] parts)
        {
            var all = new byte[parts.Sum(p => p.Length)];
            int at = 0;
            foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
            return all;
        }
    }
}
