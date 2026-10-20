using System.Diagnostics;
using System.Security.Cryptography;
using DotMarc.Backups;
using Xunit;

namespace DotMarc.Tests.Backups;

public sealed class OpenSslEncryptionTests
{
    private static readonly byte[] Data = RandomNumberGenerator.GetBytes(100_003);

    [Fact]
    public async Task RoundTrips()
    {
        var encrypted = await EncryptAsync(Data, "a long passphrase with spaces");

        Assert.Equal(Data, await DecryptAsync(encrypted, "a long passphrase with spaces"));
    }

    [Fact]
    public async Task AWrongPassphrase_IsRefused()
    {
        var encrypted = await EncryptAsync(Data, "the right passphrase");

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(encrypted, "the wrong passphrase"));
    }

    /// <summary>So a backup can be opened by hand, without dotMARC, with the command its doc comment gives.</summary>
    [Fact]
    public async Task MatchesTheOpenSslCommand_BothWays()
    {
        var directory = Directory.CreateTempSubdirectory("openssl-test-");
        try
        {
            const string passphrase = "pass phrase 'with' quotes";
            var ours = Path.Combine(directory.FullName, "ours.enc");
            await File.WriteAllBytesAsync(ours, await EncryptAsync(Data, passphrase));
            var decryptedByOpenSsl = Path.Combine(directory.FullName, "ours.out");
            await RunOpenSslAsync(passphrase, "-d", "-in", ours, "-out", decryptedByOpenSsl);
            Assert.Equal(Data, await File.ReadAllBytesAsync(decryptedByOpenSsl));

            var plain = Path.Combine(directory.FullName, "plain");
            await File.WriteAllBytesAsync(plain, Data);
            var theirs = Path.Combine(directory.FullName, "theirs.enc");
            await RunOpenSslAsync(passphrase, "-salt", "-in", plain, "-out", theirs);
            Assert.Equal(Data, await DecryptAsync(await File.ReadAllBytesAsync(theirs), passphrase));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task RunOpenSslAsync(string passphrase, params string[] arguments)
    {
        var start = new ProcessStartInfo("openssl") { RedirectStandardError = true };
        foreach (var argument in new[] { "enc", "-aes-256-cbc", "-md", "sha256", "-pbkdf2", "-iter", "200000", "-pass", "env:PASSPHRASE" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PASSPHRASE"] = passphrase;
        using var process = Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, errors);
    }

    private static async Task<byte[]> EncryptAsync(byte[] data, string passphrase)
    {
        using var output = new MemoryStream();
        await OpenSslEncryption.EncryptAsync(new MemoryStream(data), output, passphrase);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] data, string passphrase)
    {
        using var output = new MemoryStream();
        await OpenSslEncryption.DecryptAsync(new MemoryStream(data), output, passphrase);
        return output.ToArray();
    }
}
