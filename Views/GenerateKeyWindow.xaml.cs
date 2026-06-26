using System.IO;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DriveLink.Views;

public partial class GenerateKeyWindow : Window
{
    // Raw PKCS#1 PEM bytes — held in memory until the user clicks Save.
    private readonly byte[] _privateKeyPem;

    // OpenSSH public key line ("ssh-rsa AAAA... user@host") — saved alongside the private key.
    private readonly string _publicKeyLine;

    // Set after a successful save; the caller reads this back.
    public string SavedKeyPath { get; private set; } = string.Empty;

    public GenerateKeyWindow()
    {
        InitializeComponent();

        // Default save path: %USERPROFILE%\.ssh\id_rsa
        var sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        KeyPathBox.Text = Path.Combine(sshDir, "id_rsa");

        // ── Key generation ────────────────────────────────────────────────────
        using var rsa = RSA.Create(4096);

        // Build the OpenSSH public key line ("ssh-rsa AAAA... user@host").
        // We construct the binary wire format manually because RSACng (the
        // Windows implementation returned by RSA.Create) does not surface
        // ExportOpenSshPublicKey() via the RSA abstract type at compile time.
        var comment = $"{Environment.UserName}@{Environment.MachineName}";
        _publicKeyLine    = BuildSshRsaPublicKey(rsa, comment);
        PublicKeyBox.Text = _publicKeyLine;

        // Private key in PKCS#1 PEM format (-----BEGIN RSA PRIVATE KEY-----)
        // SSH.NET's PrivateKeyFile can read this directly.
        _privateKeyPem = Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem());
    }

    // ── OpenSSH public key encoding ───────────────────────────────────────────
    //
    // The authorized_keys line is:  ssh-rsa <base64(wireBytes)> comment
    //
    // wireBytes layout (all lengths as 4-byte big-endian uint):
    //   [len]["ssh-rsa"][len][exponent mpint][len][modulus mpint]
    //
    // mpint: big-endian integer, leading 0x00 prepended when MSB is set
    //        (so the value is unambiguously positive in 2's-complement).

    private static string BuildSshRsaPublicKey(RSA rsa, string comment)
    {
        var p = rsa.ExportParameters(includePrivateParameters: false);

        using var ms = new MemoryStream();

        WriteOpenSshString(ms, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteMpInt(ms, p.Exponent!);
        WriteMpInt(ms, p.Modulus!);

        var base64 = Convert.ToBase64String(ms.ToArray());
        return string.IsNullOrWhiteSpace(comment)
            ? $"ssh-rsa {base64}"
            : $"ssh-rsa {base64} {comment}";
    }

    private static void WriteOpenSshString(Stream s, byte[] data)
    {
        WriteUInt32BE(s, (uint)data.Length);
        s.Write(data);
    }

    private static void WriteMpInt(Stream s, byte[] data)
    {
        // Trim leading zero bytes (keep at least one byte).
        int start = 0;
        while (start < data.Length - 1 && data[start] == 0x00) start++;

        // Prepend 0x00 if the MSB of the first remaining byte is set,
        // ensuring the integer is interpreted as positive.
        bool needPad = (data[start] & 0x80) != 0;
        uint len = (uint)(data.Length - start + (needPad ? 1 : 0));

        WriteUInt32BE(s, len);
        if (needPad) s.WriteByte(0x00);
        s.Write(data, start, data.Length - start);
    }

    private static void WriteUInt32BE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >>  8));
        s.WriteByte((byte) v);
    }

    // ── Copy public key ───────────────────────────────────────────────────────

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PublicKeyBox.Text);

        // Brief "Copied!" feedback — revert after 1.5 s.
        CopyBtn.Content = "Copied!";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { CopyBtn.Content = "Copy Public Key"; timer.Stop(); };
        timer.Start();
    }

    // ── Browse for save location ──────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var current = KeyPathBox.Text;
        var dir     = Path.GetDirectoryName(current) ?? string.Empty;

        var dlg = new SaveFileDialog
        {
            Title            = "Save Private Key",
            FileName         = Path.GetFileName(current),
            InitialDirectory = Directory.Exists(dir) ? dir : null,
            Filter           = "Private key files|id_rsa;id_ed25519;*.pem|All files|*.*",
            OverwritePrompt  = true,
        };

        if (dlg.ShowDialog() == true)
            KeyPathBox.Text = dlg.FileName;
    }

    // ── Save private key ──────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = KeyPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show("Please choose a location to save the private key.",
                "Save Location", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Warn before overwriting an existing key pair.
        var pubPath      = path + ".pub";
        bool keyExists   = File.Exists(path);
        bool pubExists   = File.Exists(pubPath);

        if (keyExists || pubExists)
        {
            var files = keyExists && pubExists
                ? $"{Path.GetFileName(path)} and {Path.GetFileName(pubPath)}"
                : Path.GetFileName(keyExists ? path : pubPath);

            var result = MessageBox.Show(
                $"The following key file(s) already exist:\n\n" +
                $"  {files}\n\n" +
                "Overwriting will permanently replace the existing key pair. " +
                "Any servers that trust the current key will need to be updated " +
                "with the new public key.\n\n" +
                "Do you want to overwrite?",
                "Overwrite Existing Key?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;
        }

        try
        {
            // Ensure the target directory exists (creates ~/.ssh if needed).
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(path, _privateKeyPem);

            // Write the matching public key file (e.g. id_rsa.pub) so that the
            // on-disk pair stays in sync.  Without this, a pre-existing .pub file
            // would contain the OLD public key, causing fingerprint mismatches
            // when the user copies it to authorized_keys on the server.
            File.WriteAllText(pubPath, _publicKeyLine + Environment.NewLine);

            // Restrict permissions to the current user only (mirrors unix chmod 600).
            // Best-effort — if ACL manipulation fails the file is still saved.
            try
            {
                var fi  = new FileInfo(path);
                var acl = fi.GetAccessControl();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().Name,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                fi.SetAccessControl(acl);
            }
            catch { /* ACL is best-effort; proceed */ }

            SavedKeyPath = path;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save private key:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
