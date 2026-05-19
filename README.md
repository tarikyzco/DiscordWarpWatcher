# Discord WARP Watcher

A small Windows helper that keeps Discord on Cloudflare WARP without routing the rest of your computer through WARP.

Discord can be launched from the Start menu, Windows startup, Run dialog, or its normal desktop shortcut. If it starts without the proxy argument, the watcher detects the main `Discord.exe` process, closes it, prepares Cloudflare WARP in local proxy mode, and relaunches Discord with:

```text
--proxy-server=socks4://127.0.0.1:40000
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
```

WARP runs in `WarpProxy` mode, not as a system-wide tunnel. Browsers, games, Steam, YouTube, and other apps should keep using the normal internet connection unless they explicitly use `127.0.0.1:40000`.

> Use this responsibly. Check your local laws and the Discord and Cloudflare terms of service. This project is not affiliated with Discord or Cloudflare.

## What It Does

- Copies itself to:
  - `%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe`
- Adds a per-user startup entry:
  - `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DiscordWarpWatcher`
- Keeps Discord's normal startup entry:
  - `"Update.exe" --processStart Discord.exe`
- If Cloudflare WARP is missing, downloads and launches the official Windows installer from Cloudflare.
- Attempts to initialize WARP for proxy mode:
  - `warp-cli --accept-tos registration new`
  - `warp-cli --accept-tos mode proxy`
  - `warp-cli --accept-tos proxy port 40000`
  - `warp-cli --accept-tos connect`
- Watches for an unproxied main `Discord.exe` process and relaunches it through the local WARP proxy.
- Applies a small `app.asar` startup patch so Discord can launch the currently installed version when its startup update check is blocked.
  - Backup path: `app.asar.discord-warp-backup`
  - Discord updates may overwrite this patch; the watcher tries to reapply it while Discord is closed.

## What It Does Not Do

- It does not change Windows system proxy settings.
- It does not route all internet traffic through WARP.
- It does not redistribute Discord or Cloudflare binaries.
- It does not read Discord tokens, browser data, messages, passwords, or user files.

## Requirements

- Windows 10/11 x64
- Discord
- Cloudflare WARP

If WARP is not installed, the setup tries to download the official Cloudflare Windows installer and run it. Windows may ask for administrator permission for that installer.

## Build

Requires the .NET 9 SDK.

```powershell
dotnet publish .\src\DiscordWarpWatcher\DiscordWarpWatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true --source https://api.nuget.org/v3/index.json
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

Expected:

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
--proxy-server=socks4://127.0.0.1:40000
```

## Release Notes

- Do not commit `.exe` files to the repository.
- Use GitHub Releases for the built `DiscordWarpSetup.exe`.
- The app downloads Cloudflare WARP from the official Cloudflare endpoint:
  - `https://downloads.cloudflareclient.com/v1/download/windows/ga`
