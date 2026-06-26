using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;
using DriveLink.Helpers;
using DriveLink.Models;

namespace DriveLink.Services;

public sealed class ConnectionTestService
{
    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile)
    {
        var steps = new List<ConnectionTestStep>();
        string? trustedFingerprint = NormalizeHostKeyFingerprint(profile.HostKeyFingerprint);
        string? receivedFingerprint = null;
        string? hostKeyError = null;

        LoggingService.Info("ConnectionTestStarted", "Connection test started.", profile);
        AddValidationSteps(profile, steps);
        if (steps.Any(s => s.Status == ConnectionTestStatus.Failed))
        {
            var validationResult = ConnectionTestResult.From(profile, steps, trustedFingerprint, receivedFingerprint);
            LoggingService.Warn("ConnectionTestFailed",
                "Connection test failed during validation: " + FailedStepSummary(validationResult),
                profile);
            return validationResult;
        }

        await Task.Run(() =>
        {
            try
            {
                using var client = new SftpClient(BuildConnectionInfo(profile))
                {
                    OperationTimeout = TimeSpan.FromSeconds(
                        Math.Clamp(profile.OperationTimeoutSeconds, 5, 600)),
                };
                client.HostKeyReceived += (_, e) =>
                {
                    receivedFingerprint = NormalizeHostKeyFingerprint(e.FingerPrintSHA256);
                    if (string.IsNullOrWhiteSpace(trustedFingerprint))
                    {
                        e.CanTrust = true;
                        return;
                    }

                    e.CanTrust = string.Equals(
                        trustedFingerprint,
                        receivedFingerprint,
                        StringComparison.Ordinal);

                    if (!e.CanTrust)
                    {
                        hostKeyError =
                            $"Trusted fingerprint {trustedFingerprint}; received {receivedFingerprint}.";
                    }
                };

                client.Connect();
                steps.Add(ConnectionTestStep.Passed(
                    "Network",
                    $"Reached {profile.Host}:{profile.Port}."));

                if (hostKeyError != null)
                {
                    steps.Add(ConnectionTestStep.Failed(
                        "Host key",
                        "The server host key does not match the trusted fingerprint.",
                        hostKeyError));
                    return;
                }

                steps.Add(ConnectionTestStep.Passed(
                    "Host key",
                    trustedFingerprint == null
                        ? $"Received first-use fingerprint {receivedFingerprint}."
                        : $"Matched trusted fingerprint {trustedFingerprint}."));

                steps.Add(ConnectionTestStep.Passed(
                    "Authentication",
                    $"Authenticated as {profile.Username}."));

                var remotePath = NormalizeRemotePath(profile.RemotePath);
                var attrs = client.GetAttributes(remotePath);
                if (!attrs.IsDirectory)
                {
                    steps.Add(ConnectionTestStep.Failed(
                        "Remote path",
                        $"The path '{remotePath}' exists but is not a directory."));
                    return;
                }

                steps.Add(ConnectionTestStep.Passed(
                    "Remote path",
                    $"Found directory '{remotePath}'."));

                client.Disconnect();
            }
            catch (SshAuthenticationException ex)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Authentication",
                    "The server rejected the supplied credentials.",
                    ex.Message));
            }
            catch (SftpPathNotFoundException ex)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Remote path",
                    $"The path '{NormalizeRemotePath(profile.RemotePath)}' was not found.",
                    ex.Message));
            }
            catch (SftpPermissionDeniedException ex)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Remote path",
                    $"Permission was denied for '{NormalizeRemotePath(profile.RemotePath)}'.",
                    ex.Message));
            }
            catch (SshConnectionException ex) when (hostKeyError != null)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Host key",
                    "The server host key does not match the trusted fingerprint.",
                    hostKeyError));
                steps.Add(ConnectionTestStep.Failed(
                    "Network",
                    "The SSH connection was closed after the host key was rejected.",
                    ex.Message));
            }
            catch (SshConnectionException ex)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Network",
                    $"Could not reach {profile.Host}:{profile.Port}.",
                    ex.Message));
            }
            catch (SshException ex)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "SFTP",
                    "The SFTP server returned an error.",
                    ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                // Raised by PrivateKeyLoader when the key file cannot be opened or
                // read (wrong passphrase, corrupt file, or unsupported format). The
                // message is already plain English and safe to show as-is.
                steps.Add(ConnectionTestStep.Failed("Private key", ex.Message));
            }
            catch (Exception ex)
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Connection",
                    "DriveLink could not complete the connection test.",
                    ex.Message));
            }
        }).ConfigureAwait(false);

        var result = ConnectionTestResult.From(profile, steps, trustedFingerprint, receivedFingerprint);
        if (result.Succeeded)
        {
            LoggingService.Info("ConnectionTestSucceeded", "Connection test succeeded.", profile);
        }
        else
        {
            LoggingService.Warn("ConnectionTestFailed",
                "Connection test failed: " + FailedStepSummary(result),
                profile);
        }

        return result;
    }

    private static string FailedStepSummary(ConnectionTestResult result) =>
        string.Join("; ",
            result.Steps
                .Where(s => s.Status == ConnectionTestStatus.Failed)
                .Select(s => $"{s.Name}: {s.Message}"));

    private static void AddValidationSteps(ConnectionProfile profile, List<ConnectionTestStep> steps)
    {
        if (string.IsNullOrWhiteSpace(profile.Host))
            steps.Add(ConnectionTestStep.Failed("Host", "Host is required."));
        else
            steps.Add(ConnectionTestStep.Passed("Host", $"Host is set to {profile.Host}."));

        if (profile.Port is < 1 or > 65535)
            steps.Add(ConnectionTestStep.Failed("Port", "Port must be between 1 and 65535."));
        else
            steps.Add(ConnectionTestStep.Passed("Port", $"Port is set to {profile.Port}."));

        if (profile.CacheDurationSeconds is < 0 or > 300)
            steps.Add(ConnectionTestStep.Failed("Cache", "Cache duration must be between 0 and 300 seconds."));

        if (profile.ConnectionTimeoutSeconds is < 1 or > 300)
            steps.Add(ConnectionTestStep.Failed("Connect timeout", "Connect timeout must be between 1 and 300 seconds."));

        if (profile.OperationTimeoutSeconds is < 5 or > 600)
            steps.Add(ConnectionTestStep.Failed("Operation timeout", "Operation timeout must be between 5 and 600 seconds."));

        if (string.IsNullOrWhiteSpace(profile.Username))
            steps.Add(ConnectionTestStep.Failed("Username", "Username is required."));
        else
            steps.Add(ConnectionTestStep.Passed("Username", "Username is present."));

        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            if (!File.Exists(profile.PrivateKeyPath))
            {
                steps.Add(ConnectionTestStep.Failed(
                    "Private key",
                    "The private key file was not found.",
                    profile.PrivateKeyPath));
            }
            else
            {
                steps.Add(ConnectionTestStep.Passed(
                    "Private key",
                    "Private key file was found."));
            }
        }
        else if (!TryHasPassword(profile, steps))
        {
            steps.Add(ConnectionTestStep.Failed(
                "Password",
                "A password is required for password authentication."));
        }
        else
        {
            steps.Add(ConnectionTestStep.Passed(
                "Password",
                "Password is present."));
        }
    }

    private static bool TryHasPassword(ConnectionProfile profile, List<ConnectionTestStep> steps)
    {
        try
        {
            return !string.IsNullOrEmpty(CredentialHelper.Decrypt(profile.PasswordEncrypted));
        }
        catch (Exception ex)
        {
            steps.Add(ConnectionTestStep.Failed(
                "Password",
                "The saved password could not be decrypted.",
                ex.Message));
            return true;
        }
    }

    private static ConnectionInfo BuildConnectionInfo(ConnectionProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            var passphrase = CredentialHelper.Decrypt(profile.PrivateKeyPassphraseEncrypted);
            var keyFile = PrivateKeyLoader.Load(profile.PrivateKeyPath, passphrase);

            var connInfo = new ConnectionInfo(
                profile.Host,
                profile.Port,
                profile.Username,
                new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
            connInfo.Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.ConnectionTimeoutSeconds, 1, 300));
            return connInfo;
        }

        var passwordConnInfo = new ConnectionInfo(
            profile.Host,
            profile.Port,
            profile.Username,
            new PasswordAuthenticationMethod(
                profile.Username,
                CredentialHelper.Decrypt(profile.PasswordEncrypted)));
        passwordConnInfo.Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.ConnectionTimeoutSeconds, 1, 300));
        return passwordConnInfo;
    }

    private static string NormalizeRemotePath(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return ".";
        return remotePath.Trim();
    }

    private static string? NormalizeHostKeyFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;

        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            value = value["SHA256:".Length..];

        return "SHA256:" + value.Trim().TrimEnd('=');
    }
}

public sealed class ConnectionTestResult
{
    public string DisplayName { get; }
    public string Host { get; }
    public int Port { get; }
    public string Username { get; }
    public string AuthMode { get; }
    public string RemotePath { get; }
    public string? HostKeyFingerprint { get; }
    public string? TrustedHostKeyFingerprint { get; }
    public string? ReceivedHostKeyFingerprint { get; }
    public bool HasHostKeyMismatch =>
        !string.IsNullOrWhiteSpace(TrustedHostKeyFingerprint) &&
        !string.IsNullOrWhiteSpace(ReceivedHostKeyFingerprint) &&
        !string.Equals(TrustedHostKeyFingerprint, ReceivedHostKeyFingerprint, StringComparison.Ordinal);
    public IReadOnlyList<ConnectionTestStep> Steps { get; }
    public bool Succeeded => Steps.All(s => s.Status != ConnectionTestStatus.Failed);
    public string Summary => Succeeded
        ? "Connection test passed."
        : "Connection test failed.";

    private ConnectionTestResult(
        ConnectionProfile profile,
        IReadOnlyList<ConnectionTestStep> steps,
        string? trustedHostKeyFingerprint,
        string? receivedHostKeyFingerprint)
    {
        DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.Host
            : profile.DisplayName;
        Host = profile.Host;
        Port = profile.Port;
        Username = profile.Username;
        AuthMode = profile.AuthMode == DriveLink.Models.AuthMode.PrivateKey ? "Private Key" : "Password";
        RemotePath = string.IsNullOrWhiteSpace(profile.RemotePath) ? "." : profile.RemotePath;
        Steps = steps;
        TrustedHostKeyFingerprint = trustedHostKeyFingerprint;
        ReceivedHostKeyFingerprint = receivedHostKeyFingerprint;
        HostKeyFingerprint = receivedHostKeyFingerprint ?? trustedHostKeyFingerprint;
    }

    public static ConnectionTestResult From(
        ConnectionProfile profile,
        IReadOnlyList<ConnectionTestStep> steps,
        string? trustedHostKeyFingerprint,
        string? receivedHostKeyFingerprint) =>
        new(profile, steps, trustedHostKeyFingerprint, receivedHostKeyFingerprint);

    public string ToClipboardText()
    {
        var lines = new List<string>
        {
            "DriveLink Connection Test",
            $"Result: {(Succeeded ? "Passed" : "Failed")}",
            $"Connection: {DisplayName}",
            $"Host: {Host}:{Port}",
            $"Username: {Username}",
            $"Authentication: {AuthMode}",
            $"Remote path: {RemotePath}",
            $"Trusted host key fingerprint: {TrustedHostKeyFingerprint ?? "(not set)"}",
            $"Received host key fingerprint: {ReceivedHostKeyFingerprint ?? "(not received)"}",
            "",
            "Steps:"
        };

        foreach (var step in Steps)
        {
            lines.Add($"- [{step.StatusText}] {step.Name}: {step.Message}");
            if (!string.IsNullOrWhiteSpace(step.Detail))
                lines.Add($"  Detail: {step.Detail}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class ConnectionTestStep
{
    public ConnectionTestStatus Status { get; }
    public string StatusText => Status.ToString();
    public string Name { get; }
    public string Message { get; }
    public string? Detail { get; }

    private ConnectionTestStep(
        ConnectionTestStatus status,
        string name,
        string message,
        string? detail = null)
    {
        Status = status;
        Name = name;
        Message = message;
        Detail = detail;
    }

    public static ConnectionTestStep Passed(string name, string message, string? detail = null) =>
        new(ConnectionTestStatus.Passed, name, message, detail);

    public static ConnectionTestStep Failed(string name, string message, string? detail = null) =>
        new(ConnectionTestStatus.Failed, name, message, detail);
}

public enum ConnectionTestStatus
{
    Passed,
    Failed
}
