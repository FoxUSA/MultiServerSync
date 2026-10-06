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

Add your servers once. After that: check the ones you need, pick files or type a command, press **Run**.

- **Copy to every server** — files and folders go to the checked servers in parallel. A dropped connection never leaves a truncated file: each file is swapped in whole. Failed servers are retried in one click.
- **Only what changed** — compare by modification time or by SHA‑256: matching files aren't sent. Checksums can be verified again after copying.
- **Permissions, owner and date stay put** — a replaced file keeps its permissions and owner, a new one takes them from the folder it lands in. Modification time is carried over from the local file.
- **Commands and scripts** — a command or a multi‑line script of any length on every server at once, via sudo if you like. Favourite commands and history at hand.
- **Each server's output** — live, per server, with highlighting. A stuck command is interrupted with Ctrl+C or killed right from the app.
- **Tabs for different jobs** — each tab has its own servers, files, path and command. **Run everywhere** starts all tabs at once; the result shows on the tab labels.

## Get started

1. Unpack the [archive](https://byfox.dev/data/multiserversync/multiserversync.zip)
2. Run `MultiServerSync.exe`
3. Add your servers — and check where to send

Windows 10 / 11 (x64), requires [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). Freeware, no telemetry. This repository hosts the page and download links only.

---

<sub>Keywords: multi-server SSH, SFTP sync, deploy files to multiple servers, run a command on many servers, parallel SSH, Windows SSH tool.</sub>
