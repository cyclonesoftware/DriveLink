# DriveLink

[![License: PolyForm Perimeter](https://img.shields.io/badge/License-PolyForm%20Perimeter-blue.svg)](LICENSE)

Map remote SFTP servers as Windows drive letters — browse them in Explorer, open files in any application, and have them available every time you log in.

---

## What it does

DriveLink sits in your system tray and lets you connect to any SFTP server as a lettered drive (e.g. `P:\`). Once mounted, the drive behaves like any other — drag and drop, save-as dialogs, and command-line tools all work normally.

- **Password or SSH key authentication** — use an existing key or generate a new 4096-bit RSA pair directly from the app
- **Multiple connections** — add as many servers as you need, each on its own drive letter
- **Auto-connect** — optional per-connection setting to mount automatically at startup
- **Credential options** — save encrypted with Windows DPAPI, or prompt at connect time
- **Host key pinning** — TOFU fingerprint trust with manual verify and reset
- **Advanced options** — read-only mounts, cache tuning, custom drive labels, dot-file visibility
- **Runs silently** — starts to the system tray; the main window opens only when you need it

---

## Requirements

- Windows 10 or 11 (64-bit)
- [WinFsp](https://winfsp.dev/) — free, open-source kernel driver that powers drive mounting

---

## Getting a binary

This repository contains source code only. Two options:

**Build from source** (see below) — free, no sign-up required.

**Individual License** — signed installer (handles WinFsp automatically) and support. Available from [CycloneSoftware](https://drivelink.cyclonesoftwaresolutions.com).

**Enterprise package** — signed installer (handles WinFsp automatically), Group Policy ADMX templates, Intune deployment guide, and support. Available from [CycloneSoftware](https://drivelink.cyclonesoftwaresolutions.com).

---

## Build from source

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WinFsp](https://winfsp.dev/) installed on the build machine

### Publish

```powershell
dotnet publish DriveLink.csproj -c Release -r win-x64 --self-contained -o publish\
```

The output at `publish\DriveLink.exe` is self-contained and runs on any Windows 10/11 x64 machine that has WinFsp installed.

### Run directly

```powershell
dotnet run --project DriveLink.csproj
```

---

## Getting started

1. Launch `DriveLink.exe` — it starts in the system tray.
2. Right-click the tray icon and choose **Open**.
3. Click **Add Connection** and fill in your server details.
4. Click the toggle next to a connection to mount it as a drive letter.

---

## SSH key authentication

In **Edit Connection**, choose **Private Key** as the auth method, then either:

- **Browse** to an existing private key (OpenSSH or PKCS#1 PEM, PuTTY `.ppk` v2/v3 supported), or
- Click **Generate New Key Pair…** to create a fresh 4096-bit RSA pair — the public key is shown for pasting into `~/.ssh/authorized_keys`.

---

## SSH host key trust

On first connection, the server's `SHA256:` fingerprint is saved. If the server later presents a different key, DriveLink blocks the connection until you verify and accept the new fingerprint. Use **Test Connection** in the connection editor to inspect trusted vs. received fingerprints.

---

## Logs and support details

Logs are written to `%AppData%\DriveLink\Logs`. Passwords, passphrases, and private key contents are never logged. Use **Copy Support Details** from the main window to produce a redacted troubleshooting report.

---

## Issues

Bug reports are welcome via [Issues](../../issues).

---

## Development

This project uses AI assistance for tasks such as architecture planning, code review, documentation, and implementation support. All final code, design decisions, and project direction are by human maintainers

---

## License

[PolyForm Perimeter 1.0.0](LICENSE) — Copyright Cyclone Software Solutions.

Source-available: you may view, build, and modify the code for personal or internal use. You may not distribute a product that competes with DriveLink.

Built on [SSH.NET](https://github.com/sshnet/SSH.NET) and [WinFsp](https://github.com/winfsp/winfsp).
