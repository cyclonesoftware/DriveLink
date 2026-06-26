using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DriveLink.Services;

/// <summary>
/// Loads an SSH private key from disk into a <see cref="PrivateKeyFile"/>.
///
/// SSH.NET 2025.1.0 natively understands OpenSSH/PEM keys as well as PuTTY
/// <c>.ppk</c> files (both v2 and v3, the latter via its Argon2 KDF), so no
/// format conversion is required here — the original key file is read once,
/// read-only, and never copied or rewritten. This helper exists only to turn
/// SSH.NET's low-level load failures into plain-English messages that are safe
/// to show to the user.
/// </summary>
public static class PrivateKeyLoader
{
    /// <summary>
    /// Reads the private key at <paramref name="path"/>, decrypting it with
    /// <paramref name="passphrase"/> when one is supplied.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with a user-facing message when the file cannot be opened or the
    /// key cannot be read (wrong passphrase, corrupt file, or unsupported format).
    /// </exception>
    public static PrivateKeyFile Load(string path, string? passphrase)
    {
        var name = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The private key file '{path}' was not found. " +
                "Check the path in the connection settings and try again.");
        }

        try
        {
            return string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new InvalidOperationException(
                $"The private key '{name}' is protected by a passphrase. " +
                "Enter the passphrase in the connection settings and try again.", ex);
        }
        catch (SshException ex)
        {
            throw new InvalidOperationException(
                $"DriveLink could not read the private key '{name}'. " +
                "The passphrase may be incorrect, or the file may be corrupt or in " +
                "an unsupported format.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"DriveLink could not open the private key file '{path}'. " +
                $"{ex.Message}", ex);
        }
    }
}
