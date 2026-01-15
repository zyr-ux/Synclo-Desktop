using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Synclo.Services;

public interface ICryptographyService
{
    string MasterKey { get; }
    string Salt { get; }
    string KdfVersion { get; }
    byte[] DeriveAuthKey(string password, byte[] salt);
    byte[] DeriveAuthKey(ReadOnlySpan<char> password, byte[] salt);
    (byte[] authKey, byte[] wrappingKey) DerivePasswordKeys(string password, byte[] salt);
    (byte[] authKey, byte[] wrappingKey) DerivePasswordKeys(ReadOnlySpan<char> password, byte[] salt);
    byte[] GenerateMasterKey();
    byte[] WrapMasterKey(byte[] masterKey, byte[] wrappingKey);
    byte[] UnwrapMasterKey(byte[] wrappedMK, byte[] wrappingKey);
    (byte[] ciphertext, byte[] nonce) EncryptClipboard(string plaintext, byte[] masterKey);
    string DecryptClipboard(byte[] ciphertext, byte[] nonce, byte[] masterKey);
    byte[] GenerateNonce(int length = 12);
    string ToBase64(byte[] data);
    byte[] FromBase64(string base64);
}

public sealed class CryptographyService : ICryptographyService
{
    // -------------------- CONSTANTS --------------------

    // Argon2 parameters — DO NOT CHANGE
    private const int TimeCost = 2;
    private const int MemoryCost = 65536;
    private const int Parallelism = 1;

    private const int HashLength = 32;
    private const int NonceLength = 12;

    private const byte WrapFormatVersion = 1;
    private const byte KdfVersionValue = 1;

    private readonly byte[] ClipboardAad = "clipboard_v1"u8.ToArray();
    private readonly byte[] WrapAad = "wrap_mk_v1"u8.ToArray();
    private readonly byte[] HkdfSaltLabel = "hkdf_salt_v1"u8.ToArray();

    // Secure storage keys
    public string MasterKey => $"{AccountService.Prefix}.crypto.master_key";
    public string Salt => $"{AccountService.Prefix}.crypto.salt";
    public string KdfVersion => $"{AccountService.Prefix}.crypto.kdf_version";

    // -------------------- PASSWORD DERIVATION --------------------

    public byte[] DeriveAuthKey(string password, byte[] salt)
        => DerivePasswordKeys(password.AsSpan(), salt).authKey;

    public byte[] DeriveAuthKey(ReadOnlySpan<char> password, byte[] salt)
        => DerivePasswordKeys(password, salt).authKey;

    public (byte[] authKey, byte[] wrappingKey) DerivePasswordKeys(
        string password,
        byte[] salt
    ) => DerivePasswordKeys(password.AsSpan(), salt);

    public (byte[] authKey, byte[] wrappingKey) DerivePasswordKeys(
        ReadOnlySpan<char> password,
        byte[] salt
    )
    {
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (salt is null || salt.Length < 16 || salt.Length > 256)
            throw new ArgumentException("Salt must be 16–256 bytes", nameof(salt));

        var baseKey = DeriveBaseKey(password, salt);
        var hkdfSalt = DeriveHkdfSalt(salt);

        try
        {
            var authKey = new byte[HashLength];
            var wrappingKey = new byte[HashLength];

            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                baseKey,
                authKey,
                hkdfSalt,
                BuildInfo("auth_key")
            );

            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                baseKey,
                wrappingKey,
                hkdfSalt,
                BuildInfo("wrapping_key")
            );

            return (authKey, wrappingKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(baseKey);
            CryptographicOperations.ZeroMemory(hkdfSalt);
        }
    }

    private byte[] BuildInfo(string purpose)
        => Encoding.UTF8.GetBytes($"{purpose}|kdf_v{KdfVersionValue}");

    private byte[] DeriveHkdfSalt(byte[] argonSalt)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(HkdfSaltLabel, 0, HkdfSaltLabel.Length, null, 0);
        sha.TransformFinalBlock(argonSalt, 0, argonSalt.Length);
        return sha.Hash!;
    }

    private byte[] DeriveBaseKey(ReadOnlySpan<char> password, byte[] salt)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(password.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);

        try
        {
            var byteCount = Encoding.UTF8.GetBytes(password, rented);
            var pwdBytes = new byte[byteCount];
            Buffer.BlockCopy(rented, 0, pwdBytes, 0, byteCount);

            try
            {
                using var argon2 = new Argon2id(pwdBytes)
                {
                    Salt = salt,
                    DegreeOfParallelism = Parallelism,
                    MemorySize = MemoryCost,
                    Iterations = TimeCost
                };

                return argon2.GetBytes(HashLength);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pwdBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, maxBytes));
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // -------------------- MASTER KEY --------------------

    public byte[] GenerateMasterKey()
        => RandomNumberGenerator.GetBytes(32);

    public byte[] WrapMasterKey(byte[] masterKey, byte[] wrappingKey)
    {
        if (masterKey is null || masterKey.Length != 32)
            throw new ArgumentException("Invalid master key", nameof(masterKey));

        if (wrappingKey is null || wrappingKey.Length != 32)
            throw new ArgumentException("Invalid wrapping key", nameof(wrappingKey));

        var nonce = GenerateNonce();

        using var aes = new AesGcm(wrappingKey, AesGcm.TagByteSizes.MaxSize);
        var ciphertext = new byte[32];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        aes.Encrypt(nonce, masterKey, ciphertext, tag, WrapAad);

        var result = new byte[1 + nonce.Length + ciphertext.Length + tag.Length];
        result[0] = WrapFormatVersion;

        Buffer.BlockCopy(nonce, 0, result, 1, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, 1 + nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, 1 + nonce.Length + ciphertext.Length, tag.Length);

        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(tag);

        return result;
    }

    public byte[] UnwrapMasterKey(byte[] wrappedMK, byte[] wrappingKey)
    {
        if (wrappedMK is null || wrappedMK.Length != 1 + NonceLength + 32 + AesGcm.TagByteSizes.MaxSize)
            throw new ArgumentException("Invalid wrapped master key", nameof(wrappedMK));

        if (wrappedMK[0] != WrapFormatVersion)
            throw new ArgumentException("Unsupported wrap format", nameof(wrappedMK));

        if (wrappingKey is null || wrappingKey.Length != 32)
            throw new ArgumentException("Invalid wrapping key", nameof(wrappingKey));

        var nonce = new byte[NonceLength];
        var ciphertext = new byte[32];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        Buffer.BlockCopy(wrappedMK, 1, nonce, 0, nonce.Length);
        Buffer.BlockCopy(wrappedMK, 1 + nonce.Length, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(wrappedMK, 1 + nonce.Length + ciphertext.Length, tag, 0, tag.Length);

        using var aes = new AesGcm(wrappingKey, AesGcm.TagByteSizes.MaxSize);
        var plaintext = new byte[32];

        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, WrapAad);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Cryptographic operation failed", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    // -------------------- CLIPBOARD --------------------

    public (byte[] ciphertext, byte[] nonce) EncryptClipboard(
        string plaintext,
        byte[] masterKey
    )
    {
        if (string.IsNullOrEmpty(plaintext))
            throw new ArgumentException("Plaintext cannot be empty", nameof(plaintext));

        if (masterKey is null || masterKey.Length != 32)
            throw new ArgumentException("Invalid master key", nameof(masterKey));

        var nonce = GenerateNonce();
        var data = Encoding.UTF8.GetBytes(plaintext);

        using var aes = new AesGcm(masterKey, AesGcm.TagByteSizes.MaxSize);
        var ciphertext = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        try
        {
            aes.Encrypt(nonce, data, ciphertext, tag, ClipboardAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }

        var result = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, tag.Length);

        CryptographicOperations.ZeroMemory(tag);

        return (result, nonce);
    }

    public string DecryptClipboard(
        byte[] ciphertext,
        byte[] nonce,
        byte[] masterKey
    )
    {
        if (ciphertext is null || ciphertext.Length < AesGcm.TagByteSizes.MaxSize)
            throw new ArgumentException("Invalid ciphertext", nameof(ciphertext));

        if (nonce is null || nonce.Length != NonceLength)
            throw new ArgumentException("Invalid nonce", nameof(nonce));

        if (masterKey is null || masterKey.Length != 32)
            throw new ArgumentException("Invalid master key", nameof(masterKey));

        var tagLen = AesGcm.TagByteSizes.MaxSize;
        var dataLen = ciphertext.Length - tagLen;

        var data = new byte[dataLen];
        var tag = new byte[tagLen];

        Buffer.BlockCopy(ciphertext, 0, data, 0, dataLen);
        Buffer.BlockCopy(ciphertext, dataLen, tag, 0, tagLen);

        using var aes = new AesGcm(masterKey, AesGcm.TagByteSizes.MaxSize);
        var plaintext = new byte[dataLen];

        try
        {
            aes.Decrypt(nonce, data, tag, plaintext, ClipboardAad);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Cryptographic operation failed", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(data);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    // -------------------- UTIL --------------------

    public byte[] GenerateNonce(int length = NonceLength)
    {
        if (length < 8 || length > 64)
            throw new ArgumentException("Nonce length must be 8–64 bytes", nameof(length));

        return RandomNumberGenerator.GetBytes(length);
    }

    public string ToBase64(byte[] data)
        => Convert.ToBase64String(data ?? throw new ArgumentNullException(nameof(data)));

    public byte[] FromBase64(string base64)
        => Convert.FromBase64String(
            string.IsNullOrWhiteSpace(base64)
                ? throw new ArgumentException("Base64 cannot be empty", nameof(base64))
                : base64
        );
}
