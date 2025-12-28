using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Synclo.Services;

/// <summary>
/// Provides cryptographic operations for E2EE including KDF, encryption, and decryption.
/// </summary>
public sealed class CryptographyService
{
    // Argon2 parameters - MUST match server exactly
    private const int TimeCost = 2;
    private const int MemoryCost = 65536; // 64 MB
    private const int Parallelism = 1;
    private const int HashLength = 32; // 256 bits
    private const int NonceLength = 12; // Recommended for AES-GCM
    
    // Secure storage keys
    public const string MasterKey = "com.synclo.app.crypto.master_key";
    public const string Salt = "com.synclo.app.crypto.salt";
    public const string KdfVersion = "com.synclo.app.crypto.kdf_version";

    /// <summary>
    /// Derives authentication key from password using Argon2id KDF.
    /// CRITICAL: Must use exact parameters matching server.
    /// </summary>
    /// <param name="password">User password</param>
    /// <param name="salt">Salt bytes (from server or generated during registration)</param>
    /// <returns>32-byte auth key</returns>
    public byte[] DeriveAuthKey(string password, byte[] salt)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));
        
        if (salt == null || salt.Length < 16 || salt.Length > 256)
            throw new ArgumentException("Salt must be between 16 and 256 bytes", nameof(salt));

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = Parallelism,
            MemorySize = MemoryCost,
            Iterations = TimeCost
        };

        return argon2.GetBytes(HashLength);
    }

    /// <summary>
    /// Generates a new random master key for encrypting clipboard data.
    /// </summary>
    /// <returns>32-byte master key</returns>
    public byte[] GenerateMasterKey()
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>
    /// Wraps (encrypts) the master key with the authentication key using AES-256-GCM.
    /// </summary>
    /// <param name="masterKey">The master key to encrypt</param>
    /// <param name="authKey">The authentication key used for encryption</param>
    /// <returns>Nonce (12 bytes) + Ciphertext + Tag</returns>
    public byte[] WrapMasterKey(byte[] masterKey, byte[] authKey)
    {
        if (masterKey == null || masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes", nameof(masterKey));
        
        if (authKey == null || authKey.Length != 32)
            throw new ArgumentException("Auth key must be 32 bytes", nameof(authKey));

        var nonce = GenerateNonce(NonceLength);
        
        using var aesGcm = new AesGcm(authKey, AesGcm.TagByteSizes.MaxSize);
        var ciphertext = new byte[masterKey.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        
        aesGcm.Encrypt(nonce, masterKey, ciphertext, tag);

        // Combine: nonce + ciphertext + tag
        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);

        return result;
    }

    /// <summary>
    /// Unwraps (decrypts) the master key using the authentication key.
    /// </summary>
    /// <param name="wrappedMK">Wrapped master key (nonce + ciphertext + tag)</param>
    /// <param name="authKey">The authentication key used for decryption</param>
    /// <returns>Decrypted 32-byte master key</returns>
    public byte[] UnwrapMasterKey(byte[] wrappedMK, byte[] authKey)
    {
        if (wrappedMK == null || wrappedMK.Length < NonceLength + 32 + AesGcm.TagByteSizes.MaxSize)
            throw new ArgumentException("Invalid wrapped master key length", nameof(wrappedMK));
        
        if (authKey == null || authKey.Length != 32)
            throw new ArgumentException("Auth key must be 32 bytes", nameof(authKey));

        // Extract components
        var nonce = new byte[NonceLength];
        var ciphertextLength = wrappedMK.Length - NonceLength - AesGcm.TagByteSizes.MaxSize;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        Buffer.BlockCopy(wrappedMK, 0, nonce, 0, NonceLength);
        Buffer.BlockCopy(wrappedMK, NonceLength, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(wrappedMK, NonceLength + ciphertextLength, tag, 0, tag.Length);

        // Decrypt
        using var aesGcm = new AesGcm(authKey, AesGcm.TagByteSizes.MaxSize);
        var plaintext = new byte[ciphertextLength];
        
        try
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Failed to unwrap master key. Invalid auth key or corrupted data.", ex);
        }
    }

    /// <summary>
    /// Encrypts clipboard content with the master key using AES-256-GCM.
    /// </summary>
    /// <param name="plaintext">Plaintext clipboard content</param>
    /// <param name="masterKey">32-byte master key</param>
    /// <returns>Tuple of (ciphertext, nonce)</returns>
    public (byte[] ciphertext, byte[] nonce) EncryptClipboard(string plaintext, byte[] masterKey)
    {
        if (string.IsNullOrEmpty(plaintext))
            throw new ArgumentException("Plaintext cannot be empty", nameof(plaintext));
        
        if (masterKey == null || masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes", nameof(masterKey));

        var nonce = GenerateNonce(NonceLength);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        
        using var aesGcm = new AesGcm(masterKey, AesGcm.TagByteSizes.MaxSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Combine ciphertext + tag (nonce is returned separately as per server spec)
        var result = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, tag.Length);

        return (result, nonce);
    }

    /// <summary>
    /// Decrypts clipboard content with the master key.
    /// </summary>
    /// <param name="ciphertext">Ciphertext + tag</param>
    /// <param name="nonce">12-byte nonce</param>
    /// <param name="masterKey">32-byte master key</param>
    /// <returns>Decrypted plaintext string</returns>
    public string DecryptClipboard(byte[] ciphertext, byte[] nonce, byte[] masterKey)
    {
        if (ciphertext == null || ciphertext.Length < AesGcm.TagByteSizes.MaxSize)
            throw new ArgumentException("Invalid ciphertext length", nameof(ciphertext));
        
        if (nonce == null || nonce.Length != NonceLength)
            throw new ArgumentException($"Nonce must be {NonceLength} bytes", nameof(nonce));
        
        if (masterKey == null || masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes", nameof(masterKey));

        // Extract tag from end
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        var actualCiphertext = new byte[ciphertext.Length - tagLength];
        var tag = new byte[tagLength];

        Buffer.BlockCopy(ciphertext, 0, actualCiphertext, 0, actualCiphertext.Length);
        Buffer.BlockCopy(ciphertext, actualCiphertext.Length, tag, 0, tagLength);

        // Decrypt
        using var aesGcm = new AesGcm(masterKey, AesGcm.TagByteSizes.MaxSize);
        var plaintext = new byte[actualCiphertext.Length];
        
        try
        {
            aesGcm.Decrypt(nonce, actualCiphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Failed to decrypt clipboard. Invalid master key or corrupted data.", ex);
        }
    }

    /// <summary>
    /// Generates a cryptographically secure random nonce.
    /// </summary>
    /// <param name="length">Nonce length in bytes (default 12 for AES-GCM)</param>
    /// <returns>Random bytes</returns>
    public byte[] GenerateNonce(int length = NonceLength)
    {
        if (length < 8 || length > 64)
            throw new ArgumentException("Nonce length must be between 8 and 64 bytes", nameof(length));

        return RandomNumberGenerator.GetBytes(length);
    }

    /// <summary>
    /// Converts bytes to base64 string for JSON transmission.
    /// </summary>
    public string ToBase64(byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        return Convert.ToBase64String(data);
    }

    /// <summary>
    /// Converts base64 string to bytes.
    /// </summary>
    public byte[] FromBase64(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new ArgumentException("Base64 string cannot be empty", nameof(base64));

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Invalid base64 string", nameof(base64), ex);
        }
    }
}

