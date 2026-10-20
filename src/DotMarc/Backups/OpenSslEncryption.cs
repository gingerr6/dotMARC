using System.Security.Cryptography;
using System.Text;

namespace DotMarc.Backups;

/// <summary>AES-256-CBC with a PBKDF2 key, in the format of `openssl enc -aes-256-cbc -md sha256 -pbkdf2 -iter 200000
/// -salt`, so a backup can also be decrypted by hand with that command and `-d`, with nothing of dotMARC's.</summary>
public static class OpenSslEncryption
{
    public const int Iterations = 200_000;
    private static readonly byte[] Magic = "Salted__"u8.ToArray();

    public static async Task EncryptAsync(Stream plain, Stream encrypted, string passphrase, CancellationToken cancellationToken = default)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        using var aes = CreateAes(passphrase, salt);
        await encrypted.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
        await encrypted.WriteAsync(salt, cancellationToken).ConfigureAwait(false);
        await using var crypto = new CryptoStream(encrypted, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        await plain.CopyToAsync(crypto, cancellationToken).ConfigureAwait(false);
        await crypto.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Throws CryptographicException for a wrong passphrase (almost always: the padding doesn't check out) or a
    /// file that isn't one of these.</summary>
    public static async Task DecryptAsync(Stream encrypted, Stream plain, string passphrase, CancellationToken cancellationToken = default)
    {
        var header = new byte[16];
        await encrypted.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new CryptographicException("This isn't an encrypted dotMARC backup.");
        }

        using var aes = CreateAes(passphrase, header[8..]);
        await using var crypto = new CryptoStream(encrypted, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        await crypto.CopyToAsync(plain, cancellationToken).ConfigureAwait(false);
    }

    private static Aes CreateAes(string passphrase, byte[] salt)
    {
        var keyAndIv = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 48);
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keyAndIv[..32];
        aes.IV = keyAndIv[32..];
        return aes;
    }
}
