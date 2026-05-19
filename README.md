# Discord WARP Watcher

[![VirusTotal](https://img.shields.io/badge/VirusTotal-Scan_Results-blueviolet)](https://www.virustotal.com/gui/file/7f08f9a3f15f19021c9a9fba5a1791735bf023d660a27b69b48a748f5cb4ce42?nocache=1)

[🇹🇷 Türkçe (Turkish)](#türkçe) | [🇬🇧 English](#english)

---

<a name="türkçe"></a>
## 🇹🇷 Türkçe

**Discord erişim engeli kaldırma** ve **Discord yasak kaldırma** gibi ihtiyaçlar için tasarlanmış; bilgisayarınızın tüm internet trafiğini (oyunlar, tarayıcılar vb.) WARP üzerinden yönlendirmeden **sadece Discord'u** Cloudflare WARP proxy'si üzerinde tutarak kesintisiz ve hızlı erişim sağlayan küçük bir Windows yardımcı aracıdır.

Discord Başlat menüsünden, Windows başlangıcından, Çalıştır penceresinden veya normal masaüstü kısayolundan başlatılabilir. Eğer proxy argümanı olmadan başlatılırsa, izleyici (watcher) ana `Discord.exe` sürecini algılar, kapatır, Cloudflare WARP'ı yerel proxy modunda hazırlar ve Discord'u şu argümanlarla yeniden başlatır:

```text
--proxy-server=socks4://127.0.0.1:40000
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
```

WARP sistem genelinde bir tünel olarak değil, `WarpProxy` modunda çalışır. Tarayıcılar, oyunlar, Steam, YouTube ve diğer uygulamalar, `127.0.0.1:40000` adresini açıkça kullanmadıkları sürece normal internet bağlantınızı kullanmaya devam eder.

> Bunu sorumluluk bilinciyle kullanın. Yerel yasalarınızı, Discord ve Cloudflare hizmet şartlarını kontrol edin. Bu projenin Discord veya Cloudflare ile bir bağlantısı yoktur.

### Ne Yapar?

- Kendini şuraya kopyalar:
  - `%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe`
- Kullanıcıya özel bir başlangıç girdisi (startup) ekler:
  - `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DiscordWarpWatcher`
- Discord'un normal başlangıç girdisini korur:
  - `"Update.exe" --processStart Discord.exe`
- Cloudflare WARP eksikse, Cloudflare'den resmi Windows yükleyicisini indirir ve başlatır.
- WARP'ı proxy modu için başlatmaya çalışır:
  - `warp-cli --accept-tos registration new`
  - `warp-cli --accept-tos mode proxy`
  - `warp-cli --accept-tos proxy port 40000`
  - `warp-cli --accept-tos connect`
- Proxy olmadan çalışan ana `Discord.exe` sürecini izler ve yerel WARP proxy'si üzerinden yeniden başlatır.
- Discord'un başlangıç güncelleme kontrolü engellendiğinde şu anki yüklü sürümü başlatabilmesi için küçük bir `app.asar` yaması uygular.
  - Yedek yolu: `app.asar.discord-warp-backup`
  - Discord güncellemeleri bu yamayı silebilir; izleyici, Discord kapalıyken yamayı tekrar uygulamaya çalışır.

### Ne Yapmaz?

- Windows sistem proxy ayarlarını değiştirmez.
- Tüm internet trafiğini WARP üzerinden yönlendirmez.
- Discord veya Cloudflare dosyalarını yeniden dağıtmaz.
- Discord token'larını, tarayıcı verilerini, mesajları, şifreleri veya kullanıcı dosyalarını okumaz.

### Gereksinimler

- Windows 10/11 x64
- Discord
- Cloudflare WARP

Eğer WARP yüklü değilse, kurulum dosyası resmi Cloudflare Windows yükleyicisini indirip çalıştırmayı dener. Windows bu yükleyici için yönetici izni isteyebilir.

### Derleme (Build)

.NET 9 SDK gerektirir.

```powershell
dotnet publish .\src\DiscordWarpWatcher\DiscordWarpWatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true --source https://api.nuget.org/v3/index.json
```

Derleme çıktısı:

```text
src\DiscordWarpWatcher\bin\Release\net9.0-windows\win-x64\publish\DiscordWarpWatcher.exe
```

Yayın (Release) için bu dosyayı `DiscordWarpSetup.exe` olarak paylaşabilirsiniz.

### Kurulum (Install)

`DiscordWarpSetup.exe` dosyasını bir kez çalıştırın.

Kurulumdan sonra Discord'u normal şekilde açın. İzleyici arka planda çalışır ve yalnızca Discord'a WARP proxy argümanını eklemesi gerektiğinde onu yeniden başlatır.

### Kaldırma (Uninstall)

```powershell
%LOCALAPPDATA%\DiscordWarp\DiscordWarpWatcher.exe --uninstall
```

Bundan sonra şu klasörü silebilirsiniz:

```text
%LOCALAPPDATA%\DiscordWarp
```

Discord'un `app.asar` yedeğini geri yüklemek için Discord'u kapatın ve şunu çalıştırın:

```powershell
Copy-Item "$env:LOCALAPPDATA\Discord\app-<version>\resources\app.asar.discord-warp-backup" "$env:LOCALAPPDATA\Discord\app-<version>\resources\app.asar" -Force
```

`<version>` kısmını yüklü olan Discord uygulama klasörüyle değiştirin, örneğin `app-1.0.9237`.

### Doğrulama (Verify)

Normal internet trafiğinin WARP üzerinden gitmediğini kontrol edin:

```powershell
curl.exe -s https://www.cloudflare.com/cdn-cgi/trace | Select-String '^warp='
```

Beklenen:

```text
warp=off
```

WARP proxy modunun Discord'a ulaşabildiğini kontrol edin:

```powershell
curl.exe -s --socks4 127.0.0.1:40000 https://discord.com/api/v10/gateway
```

Beklenen:

```json
{"url":"wss://gateway.discord.gg"}
```

Discord'un proxy argümanıyla çalıştığını kontrol edin:

```powershell
Get-CimInstance Win32_Process -Filter "name = 'Discord.exe'" |
  Where-Object { $_.CommandLine -notmatch '--type=' } |
  Select-Object ProcessId, CommandLine
```

Komut satırı şunları içermelidir:

```text
--proxy-server=socks4://127.0.0.1:40000
```

### Güvenlik ve VirusTotal (False Positives)

Program imzasız açık kaynaklı bir .NET uygulaması olduğu ve arka planda Discord'u kapatıp açma, başlangıca (Registry) ekleme, internetten Cloudflare WARP indirme gibi işlemler yaptığı için VirusTotal'de birkaç bilinmeyen antivirüs motoru "False Positive" (Yanlış Pozitif) uyarılar verebilir. Büyük antivirüsler (Windows Defender, Kaspersky vb.) dosyayı tamamen temiz bulmaktadır. Kaynak kodları tamamen açıktır, dileyen kodları satır satır inceleyip kendi bilgisayarında baştan derleyebilir.

### Sürüm Notları (Release Notes)

- `.exe` dosyalarını depoya (repository) commit etmeyin.
- Derlenmiş `DiscordWarpSetup.exe` için GitHub Releases kullanın.
- Uygulama, Cloudflare WARP'ı resmi Cloudflare adresinden indirir:
  - `https://downloads.cloudflareclient.com/v1/download/windows/ga`

---

<a name="english"></a>
## 🇬🇧 English

A small Windows helper designed to **unblock Discord** and bypass restrictions. It keeps Discord running securely on a Cloudflare WARP proxy without routing the rest of your computer's internet traffic (games, browsers, etc.) through the VPN/WARP.

Discord can be launched from the Start menu, Windows startup, Run dialog, or its normal desktop shortcut. If it starts without the proxy argument, the watcher detects the main `Discord.exe` process, closes it, prepares Cloudflare WARP in local proxy mode, and relaunches Discord with:

```text
--proxy-server=socks4://127.0.0.1:40000
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
```

WARP runs in `WarpProxy` mode, not as a system-wide tunnel. Browsers, games, Steam, YouTube, and other apps should keep using the normal internet connection unless they explicitly use `127.0.0.1:40000`.

> Use this responsibly. Check your local laws and the Discord and Cloudflare terms of service. This project is not affiliated with Discord or Cloudflare.

### What It Does

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

### What It Does Not Do

- It does not change Windows system proxy settings.
- It does not route all internet traffic through WARP.
- It does not redistribute Discord or Cloudflare binaries.
- It does not read Discord tokens, browser data, messages, passwords, or user files.

### Requirements

- Windows 10/11 x64
- Discord
- Cloudflare WARP

If WARP is not installed, the setup tries to download the official Cloudflare Windows installer and run it. Windows may ask for administrator permission for that installer.

### Build

Requires the .NET 9 SDK.

```powershell
dotnet publish .\src\DiscordWarpWatcher\DiscordWarpWatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true --source https://api.nuget.org/v3/index.json
```

Build output:

```text
src\DiscordWarpWatcher\bin\Release\net9.0-windows\win-x64\publish\DiscordWarpWatcher.exe
```

For releases, publish that file as `DiscordWarpSetup.exe`.

### Install

Run `DiscordWarpSetup.exe` once.

After setup, open Discord normally. The watcher runs in the background and only relaunches Discord when it needs to add the WARP proxy argument.

### Uninstall

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

### Verify

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

### Security and VirusTotal (False Positives)

Because this is an unsigned, open-source .NET application that performs background tasks such as restarting Discord, adding registry startup entries, and downloading Cloudflare WARP, a few lesser-known antivirus engines on VirusTotal might flag it as a "False Positive". Major antivirus engines (like Windows Defender, Kaspersky, etc.) report it as completely clean. The source code is open for review, and you are welcome to compile it yourself from scratch.

### Release Notes

- Do not commit `.exe` files to the repository.
- Use GitHub Releases for the built `DiscordWarpSetup.exe`.
- The app downloads Cloudflare WARP from the official Cloudflare endpoint:
  - `https://downloads.cloudflareclient.com/v1/download/windows/ga`
