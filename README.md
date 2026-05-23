# Discord WARP Watcher

A small Windows helper that keeps **only Discord** on Cloudflare WARP while leaving the rest of your internet connection untouched.

Discord can be launched from the Start menu, Windows startup, the Run dialog, or the normal Discord shortcut. If Discord starts without the proxy argument, the watcher detects the main `Discord.exe` process, closes it, prepares Cloudflare WARP in local proxy mode, and relaunches Discord through WARP.

```text
--proxy-server=socks4://127.0.0.1:<selected-port>
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
```

The default WARP proxy port is `40000`. If that port is already busy, the watcher automatically tries a free fallback port between `40001` and `40100`.

> This project is not affiliated with Discord or Cloudflare. Use it responsibly and check your local laws and the Discord and Cloudflare terms of service.

## What It Does

- Copies itself to:
  - `%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe`
- Adds a per-user startup entry:
  - `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DiscordWarpWatcher`
- Keeps Discord's normal startup entry when it can find `Update.exe`.
- Downloads and launches the official Cloudflare WARP Windows installer if WARP is missing.
- Attempts to initialize WARP:
  - `warp-cli --accept-tos registration new`
  - `warp-cli --accept-tos mode proxy`
  - `warp-cli --accept-tos proxy port <selected-port>`
  - `warp-cli --accept-tos connect`
- Watches for an unproxied main `Discord.exe` process and relaunches Discord through the selected local WARP proxy port.
- Searches for Discord in multiple locations:
  - `%LOCALAPPDATA%\Discord`
  - `%LOCALAPPDATA%\DiscordCanary`
  - `%LOCALAPPDATA%\DiscordPTB`
  - `%ProgramFiles%\Discord`
  - `%ProgramFiles(x86)%\Discord`
  - Discord registry uninstall/startup entries
  - currently running Discord processes
- Applies a small `app.asar` startup patch when possible, so the currently installed Discord version can launch even if the startup update check is blocked.
  - Backup path: `app.asar.discord-warp-backup`
  - If Discord is installed under a protected folder such as `C:\Program Files`, Windows may ask for administrator permission for this patch.

## What It Does Not Do

- It does not change Windows system proxy settings.
- It does not route all internet traffic through WARP.
- It does not redistribute Discord or Cloudflare binaries.
- It does not read Discord tokens, browser data, messages, passwords, or user files.

## Requirements

- Windows 10/11 x64
- Discord
- Cloudflare WARP

If WARP is not installed, the setup downloads the official Cloudflare installer from:

```text
https://downloads.cloudflareclient.com/v1/download/windows/ga
```

Windows may ask for administrator permission to install WARP.

Cloudflare WARP installs a Windows network service/driver, so it cannot be installed without administrator permission. Some sandbox environments block UAC/admin prompts; in that case the watcher keeps the downloaded MSI and opens its location instead of silently pretending the install succeeded.

## Build

Requires the .NET 9 SDK.

```powershell
dotnet publish .\src\DiscordWarpWatcher\DiscordWarpWatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --source https://api.nuget.org/v3/index.json
```

Build output:

```text
src\DiscordWarpWatcher\bin\Release\net9.0-windows\win-x64\publish\DiscordWarpWatcher.exe
```

For releases, publish that file as `DiscordWarpSetup.exe`.

## Install

Run `DiscordWarpSetup.exe` once.

After setup, open Discord normally. The watcher runs in the background and only relaunches Discord when it needs to add the WARP proxy argument.

## Uninstall

```powershell
%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe --uninstall
```

After that, you can delete:

```text
%LOCALAPPDATA%\DiscordWarp
```

To restore Discord's `app.asar` backup, close Discord and run:

```powershell
Copy-Item "$env:LOCALAPPDATA\Discord\app-<version>\resources\app.asar.discord-warp-backup" "$env:LOCALAPPDATA\Discord\app-<version>\resources\app.asar" -Force
```

Replace `<version>` with the installed Discord app folder, for example `app-1.0.9237`.

## Diagnostics

Generate a local diagnostics file:

```powershell
%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe --diagnose
```

This writes and opens:

```text
%LOCALAPPDATA%\DiscordWarp\diagnostics.txt
```

The diagnostics file includes WARP status, WARP settings, selected proxy port information, Discord paths, startup entries, and running Discord process command lines.

## Verify

Check that normal internet traffic is not going through WARP:

```powershell
curl.exe -s https://www.cloudflare.com/cdn-cgi/trace | Select-String '^warp='
```

Expected:

```text
warp=off
```

Check that WARP proxy mode can reach Discord:

```powershell
curl.exe -s --socks4 127.0.0.1:40000 https://discord.com/api/v10/gateway
```

If port `40000` was busy, check the selected fallback port in:

```text
%LOCALAPPDATA%\DiscordWarp\watcher.log
```

Expected response:

```json
{"url":"wss://gateway.discord.gg"}
```

Check that Discord is running with the proxy argument:

```powershell
Get-CimInstance Win32_Process -Filter "name = 'Discord.exe'" |
  Where-Object { $_.CommandLine -notmatch '--type=' } |
  Select-Object ProcessId, CommandLine
```

The command line should include:

```text
--proxy-server=socks4://127.0.0.1:<selected-port>
```

## Troubleshooting

### Port 40000 is already busy

The watcher now detects this case automatically. It first tries `40000`, then falls back to a free port between `40001` and `40100`.

Check the selected port:

```powershell
Get-Content "$env:LOCALAPPDATA\DiscordWarp\watcher.log" -Tail 50
```

### Discord randomly pops to the foreground

Older builds could relaunch Discord too aggressively when WARP changed between the preferred port and a fallback port. Current builds treat any managed WARP proxy port between `40000` and `40100` as valid, avoid killing Discord when a healthy proxied Discord process is already running, and start automatic recovery relaunches minimized.

Update to the latest build if your log repeatedly shows:

```text
Unproxied Discord detected. Restarting through WARP proxy.
```

### Discord is installed in a different folder

The watcher searches common per-user, system-wide, registry, and running-process locations. If Discord still cannot be found, run diagnostics:

```powershell
%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe --diagnose
```

### app.asar patch fails

If Discord is installed under `C:\Program Files`, patching `app.asar` may require administrator permission. The watcher will request elevation when needed. If the prompt is denied, Discord may still fail at the startup update screen.

### Windows Defender or antivirus blocks the app

This app is unsigned and performs behavior that security tools may consider sensitive:

- adds a startup registry entry
- downloads the official Cloudflare WARP installer
- closes and relaunches Discord
- patches Discord's local `app.asar`

If your antivirus quarantines it, restore the file only if you trust the source. You can also build the app yourself from this repository.

### WARP registration or connection fails

Some school, work, or restricted networks may block Cloudflare WARP registration or connection. Check:

```powershell
warp-cli --version
warp-cli status
warp-cli settings
```

If registration fails, open the Cloudflare WARP app manually once and complete onboarding, then run Discord normally again.

### WARP installation permission was denied

If WARP was not installed because the Windows administrator prompt was denied, install Cloudflare WARP manually from:

```text
https://one.one.one.one/
```

Then run `DiscordWarpSetup.exe` again.

The setup writes the MSI installer log here:

```text
%LOCALAPPDATA%\DiscordWarp\cloudflare-warp-install.log
```

It also keeps the downloaded installer here:

```text
%LOCALAPPDATA%\DiscordWarp\Cloudflare_WARP_Release-x64.msi
```

The setup tries multiple installation methods:

- elevated passive `msiexec`
- non-elevated `msiexec` fallback
- interactive MSI launch
- `winget install --id Cloudflare.Warp` if winget is available

If all methods fail in a sandbox, run the setup on a normal Windows desktop or install WARP manually with administrator permission.

## Security and False Positives

The source code is public and reviewable. The release executable is unsigned, so some antivirus vendors may flag it. For maximum trust, clone the repository and build it locally.

Do not commit `.exe` files to the repository. Use GitHub Releases for `DiscordWarpSetup.exe`.
