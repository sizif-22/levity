using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class RelayCryptoTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static string Frame(string json = "{\"type\":\"status\"}", string direction = RelayCrypto.HostToPhone, string session = "sess", long n = 1) =>
        RelayCrypto.Encrypt(Key, direction, session, n, json);

    [Fact]
    public void Round_trips_a_message()
    {
        string frame = Frame("{\"type\":\"user_message\",\"text\":\"héllo\"}", session: "abc", n: 7);
        Assert.True(RelayCrypto.TryDecrypt(Key, RelayCrypto.HostToPhone, frame, out string s, out long n, out var msg));
        Assert.Equal("abc", s);
        Assert.Equal(7, n);
        Assert.Equal("héllo", msg.GetProperty("text").GetString());
    }

    [Fact]
    public void Every_frame_is_different_even_for_the_same_message()
    {
        Assert.NotEqual(Frame(), Frame());
    }

    [Fact]
    public void The_wrong_direction_is_rejected_so_frames_cannot_be_reflected_back()
    {
        string frame = Frame(direction: RelayCrypto.HostToPhone);
        Assert.False(RelayCrypto.TryDecrypt(Key, RelayCrypto.PhoneToHost, frame, out _, out _, out _));
    }

    [Fact]
    public void A_different_key_is_rejected()
    {
        byte[] other = RelayCrypto.NewKey();
        Assert.False(RelayCrypto.TryDecrypt(other, RelayCrypto.HostToPhone, Frame(), out _, out _, out _));
    }

    [Fact]
    public void A_tampered_frame_is_rejected()
    {
        byte[] raw = RelayCrypto.FromBase64Url(Frame());
        raw[20] ^= 0x01;
        Assert.False(RelayCrypto.TryDecrypt(Key, RelayCrypto.HostToPhone, RelayCrypto.ToBase64Url(raw), out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void Garbage_is_rejected_without_throwing(string frame)
    {
        Assert.False(RelayCrypto.TryDecrypt(Key, RelayCrypto.HostToPhone, frame, out _, out _, out _));
    }

    [Fact]
    public void A_stale_frame_is_rejected_even_when_it_authenticates()
    {
        long old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        string plain = $"{{\"s\":\"s\",\"n\":1,\"t\":{old},\"m\":{{}}}}";
        Assert.False(RelayCrypto.TryDecrypt(Key, RelayCrypto.HostToPhone, Seal(plain), out _, out _, out _));
    }

    [Fact]
    public void A_frame_from_the_far_future_is_rejected_too()
    {
        long future = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        string plain = $"{{\"s\":\"s\",\"n\":1,\"t\":{future},\"m\":{{}}}}";
        Assert.False(RelayCrypto.TryDecrypt(Key, RelayCrypto.HostToPhone, Seal(plain), out _, out _, out _));
    }

    [Fact]
    public void A_frame_without_a_session_id_is_rejected()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string plain = $"{{\"s\":\"\",\"n\":1,\"t\":{now},\"m\":{{}}}}";
        Assert.False(RelayCrypto.TryDecrypt(Key, RelayCrypto.HostToPhone, Seal(plain), out _, out _, out _));
    }

    [Fact]
    public void Room_id_is_32_lowercase_hex_chars_derived_from_the_key_only()
    {
        string id = RelayCrypto.RoomId(Key);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, RelayCrypto.RoomId(Key));
        Assert.NotEqual(id, RelayCrypto.RoomId(RelayCrypto.NewKey()));
        Assert.DoesNotContain(Convert.ToHexString(Key).ToLowerInvariant()[..16], id); // the relay must not learn the key
    }

    [Fact]
    public void New_keys_are_32_random_bytes()
    {
        byte[] a = RelayCrypto.NewKey(), b = RelayCrypto.NewKey();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Base64url_round_trips_all_byte_values()
    {
        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        string text = RelayCrypto.ToBase64Url(all);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.DoesNotContain('=', text);
        Assert.Equal(all, RelayCrypto.FromBase64Url(text));
    }

    /// <summary>Builds an authentic frame around an arbitrary plaintext, to test the checks after decryption.</summary>
    private static string Seal(string plain)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(12), data = Encoding.UTF8.GetBytes(plain), cipher = new byte[data.Length], tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(iv, data, cipher, tag, Encoding.UTF8.GetBytes(RelayCrypto.HostToPhone));
        return RelayCrypto.ToBase64Url(iv.Concat(cipher).Concat(tag).ToArray());
    }
}

/// <summary>The PC (C#) and the phone app (TypeScript) must agree byte for byte. Uses the real web/src/lib/crypto.ts via Node.</summary>
public class CryptoInteropTests
{
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "web", "src", "lib", "crypto.ts"))) return dir.FullName;
        return null;
    }

    [Fact]
    public async Task Frames_and_room_ids_are_compatible_in_both_directions()
    {
        string? root = FindRepoRoot();
        Assert.True(root != null, "repo root with web/src/lib/crypto.ts not found");
        string script = Path.Combine(root!, "tests", "Levity.Tests", "interop", "crypto-interop.ts");

        byte[] key = RelayCrypto.NewKey();
        string hostFrame = RelayCrypto.Encrypt(key, RelayCrypto.HostToPhone, "host-session", 1, "{\"type\":\"status\",\"text\":\"Ready ✓\"}");
        var input = JsonSerializer.Serialize(new { keyHex = Convert.ToHexString(key).ToLowerInvariant(), hostFrame, phoneMessage = new { type = "send_prompt", text = "hi from phone" } });

        var psi = new ProcessStartInfo("node", $"--experimental-strip-types --no-warnings \"{script}\"")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = root,
        };
        using var node = Process.Start(psi)!;
        await node.StandardInput.WriteAsync(input);
        node.StandardInput.Close();
        string stdout = await node.StandardOutput.ReadToEndAsync();
        string stderr = await node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync();
        Assert.True(node.ExitCode == 0, $"node failed: {stderr}");

        using var result = JsonDocument.Parse(stdout);
        var r = result.RootElement;

        Assert.Equal(RelayCrypto.RoomId(key), r.GetProperty("roomId").GetString());                       // same room id on both sides
        Assert.Equal("Ready ✓", r.GetProperty("opened").GetProperty("text").GetString());                    // phone opened the PC's frame

        Assert.True(RelayCrypto.TryDecrypt(key, RelayCrypto.PhoneToHost, r.GetProperty("phoneFrame").GetString()!, out _, out long n, out var msg)); // PC opened the phone's frame
        Assert.Equal(1, n);
        Assert.Equal("hi from phone", msg.GetProperty("text").GetString());
    }
}
