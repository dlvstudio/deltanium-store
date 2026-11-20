Deltanium Store node for Windows, Linux and macOS: self-contained, no .NET install needed.

1. Download the package for your system and extract it.
2. Run the installer: **Windows** `install.ps1` in PowerShell as administrator; **Linux / macOS** `sudo ./install.sh`.
3. Answer: network (mainnet / testnet), your one-shot voucher, and how the network reaches you (`auto` uses your
   public IP over DLTP, TCP port 7333: no domain needed).
4. Forward TCP port 7333 on your router to the computer. Check https://deltanium.com/nodes.html.

The installer shows the network's Central and Super Admin keys: compare them with NODE_KEYS.md before you continue.
Your node key stays in the data folder (service account and administrators only); back it up.

| Package | System |
|---|---|
| `deltanium-store-win-x64.zip` | Windows 10/11, Windows Server (64-bit) |
| `deltanium-store-linux-x64.tar.gz` | Linux x64 with systemd |
| `deltanium-store-linux-arm64.tar.gz` | Linux ARM64 (e.g. Raspberry Pi 4/5 with a 64-bit OS) |
| `deltanium-store-osx-arm64.tar.gz` | macOS on Apple Silicon |
| `deltanium-store-osx-x64.tar.gz` | macOS on Intel |

Check downloads against `SHA256SUMS.txt`. Guide: https://deltanium.com/node-setup.html
