using System.Security.Cryptography;
using System.Text;

namespace AppLockerOverlay;

/// <summary>AES-GCM encrypted payload for the whole settings file (opaque on disk).</summary>
internal static class ConfigCrypto
{
    private static ReadOnlySpan<byte> Magic => "ALC1"u8;
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 120_000;
    private const int KeySize = 32;

    public static bool IsEncryptedPayload(ReadOnlySpan<byte> data)
    {
        return data.Length >= Magic.Length + 1 + SaltSize + NonceSize + TagSize + 1
               && data.StartsWith(Magic);
    }

    public static byte[] EncryptUtf8(string plaintext, string masterPassword)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(masterPassword),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
        var tag = new byte[TagSize];
        var cipher = new byte[plainBytes.Length];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(key);

        var result = new byte[Magic.Length + 1 + SaltSize + NonceSize + TagSize + cipher.Length];
        var o = 0;
        Magic.CopyTo(result.AsSpan(o));
        o += Magic.Length;
        result[o++] = Version;
        salt.AsSpan().CopyTo(result.AsSpan(o));
        o += SaltSize;
        nonce.AsSpan().CopyTo(result.AsSpan(o));
        o += NonceSize;
        tag.AsSpan().CopyTo(result.AsSpan(o));
        o += TagSize;
        cipher.AsSpan().CopyTo(result.AsSpan(o));
        return result;
    }

    public static string DecryptToUtf8(ReadOnlySpan<byte> payload, string masterPassword)
    {
        if (!IsEncryptedPayload(payload))
        {
            throw new InvalidOperationException("Not an encrypted Windlock settings file.");
        }

        var o = Magic.Length;
        if (payload[o++] != Version)
        {
            throw new InvalidDataException("Unsupported settings file version.");
        }

        var salt = payload.Slice(o, SaltSize);
        o += SaltSize;
        var nonce = payload.Slice(o, NonceSize);
        o += NonceSize;
        var tag = payload.Slice(o, TagSize);
        o += TagSize;
        var cipher = payload[o..];

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(masterPassword),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        var plain = new byte[cipher.Length];
        try
        {
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return Encoding.UTF8.GetString(plain);
    }
}
