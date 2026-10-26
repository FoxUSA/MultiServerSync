<div align="center">

<img src="images/fox.png" width="96" alt="MultiServerSync logo">

# MultiServerSync

**Files and commands on every server at once.**

Check the servers — and push files or run a command on all of them with one click. Every server reports back separately.

[![Download .zip](https://img.shields.io/badge/Download-multiserversync.zip-2ea44f?style=for-the-badge)](https://byfox.dev/data/multiserversync/multiserversync.zip)
[![Website](https://img.shields.io/badge/Website-byfox.dev-4ecdc4?style=for-the-badge)](https://byfox.dev/multiserversync/)

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows)
![SSH / SFTP](https://img.shields.io/badge/SSH%20%2F%20SFTP-Linux%20servers-444)
![Free](https://img.shields.io/badge/price-free-2ea44f)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

**English** · [Русский](README.ru.md)

</div>

<p align="center">
<a href="images/multiserversync-copy.png"><img src="images/multiserversync-copy.png" height="140" alt="Copying"></a>
<a href="images/multiserversync-copy-output.png"><img src="images/multiserversync-copy-output.png" height="140" alt="Copy output"></a>
<a href="images/multiserversync-command.png"><img src="images/multiserversync-command.png" height="140" alt="Command"></a>
<a href="images/multiserversync-command-output.png"><img src="images/multiserversync-command-output.png" height="140" alt="Command output"></a>
<a href="images/multiserversync-servers.png"><img src="images/multiserversync-servers.png" height="140" alt="Servers"></a>
<a href="images/multiserversync-tabs.png"><img src="images/multiserversync-tabs.png" height="140" alt="All tabs"></a>
</p>

## What it's for

- **Hundreds of routes, one click.** A tab is a scenario: "this folder → there, on these servers", "that file → elsewhere". **Run everywhere** fires every tab at once.
- **Copy, then run commands.** A switch on a copy tab: after a successful upload its commands run — only on the servers where the copy went through.
- **Commands in parallel.** Command tabs run side by side — even different commands on the same server: logs in one, restart in another.
- **Fast copy, verified twice.** SHA-256 before sending — identical files stay put; after — proof everything arrived intact.
- **No wrong server.** Smart mode remembers where each command and tab usually goes and asks before an unusual target.

Add your servers once. After that: check the ones you need, pick files or type a command, press **Run**.

- **Copy to every server** — files and folders go to the checked servers in parallel. A dropped connection never leaves a truncated file: each file is swapped in whole. Failed servers are retried in one click.
- **Many streams** — small files travel as compressed tar batches over three streams, large files in chunks over four connections. No rsync needed on the server.
- **Only what changed** — compare by modification time or by SHA-256: matching files aren't sent. Checksums can be verified again after copying.
- **Permissions, owner and date stay put** — a replaced file keeps its permissions and owner, a new one takes them from the folder it lands in.
- **Download** — from servers to your PC the same way: only what changed, checksum check; from several servers — each into its own subfolder. Sizes and dates in the list refresh from the servers on their own.
- **Explorer** — two large panes, your PC and a server, like WinSCP: Enter, F2, F5 copy, Del; as many tabs as you like. Permissions and owner change only in the Properties window. A server file opens in any Windows app and goes back on save.
- **Edit in place** — configs and scripts open in a built-in editor with line numbers, highlighting and find/replace. Encoding, line endings, owner and permissions stay as they were; the file is checked by SHA-256 after saving.
- **Exclusions** — masks like `node_modules` and `*.log` stay out both ways.
- **Commands and scripts** — several commands in one tab run in turn: the next one after the previous succeeds. A script of any length goes whole through stdin; answer `[Y/n]` from the app, the sudo password is supplied for you. Command sets go to favourites.
- **Each server's output** — live and separate, highlighted, with a zoom for larger text. Copy progress with time left; one **Stop** button.
- **See what's wrong** — a persistent notice when commands after copying won't run: the tab is empty or the servers differ. Errors in plain words.
- **Tabs for different jobs** — each tab has its own servers, files, path and command. **Run everywhere** starts all tabs at once; the result shows on the tab labels. Tabs you rarely need go to an archive strip with a right-click and keep working there.
- **Connection** — OpenSSH and PuTTY keys or a password, login through a jump host, import from `~/.ssh/config`, PuTTY and WinSCP. The server's host key is remembered — a swap gets noticed.
- **Passwords encrypted** — under your Windows account or with a master password.
- **Smart mode** — asks if a command or copy is about to run on a server it doesn't usually go to.

## Get started

1. Unpack the [archive](https://byfox.dev/data/multiserversync/multiserversync.zip)
2. Run `MultiServerSync.exe`
3. Add your servers — and check where to send

Windows 10 / 11 (x64), requires [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). Free, open source, no telemetry.

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/FoxUSA/MultiServerSync.git
cd MultiServerSync
powershell -ExecutionPolicy Bypass -File .\Data\build.ps1
```

The launcher `MultiServerSync.exe` appears in the repository root, the program in `Data`, your settings in `UserData`. Sources: `Data/src/App` (the program, WPF + [WPF UI](https://github.com/lepoco/wpfui), [SSH.NET](https://github.com/sshnet/SSH.NET), [AvalonEdit](https://github.com/icsharpcode/AvalonEdit)) and `Data/src/Launcher`. Developer notes (in Russian): [Data/README.md](Data/README.md).

## License

[GPL-3.0](LICENSE) — use, change and share freely; programs built on this code must stay open source too.

---

<sub>Keywords: multi-server SSH, SFTP sync, deploy files to multiple servers, run a command on many servers, parallel SSH, download from multiple servers, two-pane SFTP explorer, Windows SSH tool.</sub>
