using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Subsonic.Store;

/// <summary>
/// AES-256-GCM encrypt/decrypt for DB storage.
/// Wire format: IV (12) | AuthTag (16) | Ciphertext.
/// Key derived from plugin salt via Rfc2898 (PBKDF2-SHA256, 100k iterations).
/// </summary>
public static class Crypto
{
    private const int IvLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;
    private const string KdfSalt = "subfin-db-encryption-v1";

    // Derived keys, cached per salt (deriving takes 100k PBKDF2 iterations).
    private static (string Salt, byte[] Key)? _cachedKey;
    private static (string Salt, byte[] Key)? _cachedLookupKey;

    public static void SetSalt(string base64Salt)
    {
        _GetKey(base64Salt); // pre-warm
    }

    /// <summary>
    /// Keyed hash (HMAC-SHA256) of an app password, stored so it can be looked up as an API key
    /// without a username. The passwords are 144-bit random values, so a fast hash is enough.
    /// Its key is derived from, but independent of, the encryption key.
    /// </summary>
    public static string LookupHash(string secret, string salt)
    {
        if (_cachedLookupKey is not { } cached || cached.Salt != salt)
        {
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, _GetKey(salt), KeyLen, info: Encoding.UTF8.GetBytes("subfin-api-key-lookup-v1"));
            _cachedLookupKey = cached = (salt, key);
        }
        return Convert.ToHexString(HMACSHA256.HashData(cached.Key, Encoding.UTF8.GetBytes(secret)));
    }

    private static byte[] _GetKey(string base64Salt)
    {
        if (_cachedKey is { } cached && cached.Salt == base64Salt) return cached.Key;
        var saltBytes = Encoding.UTF8.GetBytes(KdfSalt);
        var password = Convert.FromBase64String(base64Salt);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 100_000, HashAlgorithmName.SHA256, KeyLen);
        _cachedKey = (base64Salt, key);
        return key;
    }

    public static byte[] Encrypt(string plaintext, string salt)
    {
        var key = _GetKey(salt);
        var iv = RandomNumberGenerator.GetBytes(IvLen);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLen];

        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(iv, plaintextBytes, ciphertext, tag);

        var result = new byte[IvLen + TagLen + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, result, 0, IvLen);
        Buffer.BlockCopy(tag, 0, result, IvLen, TagLen);
        Buffer.BlockCopy(ciphertext, 0, result, IvLen + TagLen, ciphertext.Length);
        return result;
    }

    public static string Decrypt(byte[] blob, string salt)
    {
        if (blob.Length < IvLen + TagLen)
            throw new ArgumentException("Invalid encrypted blob");

        var key = _GetKey(salt);
        var iv = blob[..IvLen];
        var tag = blob[IvLen..(IvLen + TagLen)];
        var ciphertext = blob[(IvLen + TagLen)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagLen);
        aes.Decrypt(iv, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
