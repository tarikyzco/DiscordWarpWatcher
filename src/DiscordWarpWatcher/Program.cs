using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows.Forms;

namespace DiscordWarpWatcher;

internal static class Program
{
    private const int PreferredWarpSocksPort = 40000;
    private const int FallbackWarpSocksPortStart = 40001;
    private const int FallbackWarpSocksPortEnd = 40100;
    private const string ProxyScheme = "socks4";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppFolderName = "DiscordWarp";
    private const string InstalledExeName = "DiscordWarpWatcher.exe";
    private const string WatcherRunValue = "DiscordWarpWatcher";
    private const string OldLauncherRunValue = "DiscordWarp";
    private const string DiscordRunValue = "Discord";
    private const string PatchMarker = "DiscordWarpWatcherPatch";
    private const string WarpDownloadUrl = "https://downloads.cloudflareclient.com/v1/download/windows/ga";
    private const string WarpInstallerName = "Cloudflare_WARP_Release-x64.msi";

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    private static readonly string InstalledExe = Path.Combine(InstallDir, InstalledExeName);
    private static readonly string LogPath = Path.Combine(InstallDir, "watcher.log");
    private static int CurrentWarpSocksPort = PreferredWarpSocksPort;

    [STAThread]
    private static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(InstallDir);

        if (args.Length >= 2 && args[0].Equals("--patch-asar", StringComparison.OrdinalIgnoreCase))
        {
            PatchDiscordAsarFile(args[1], allowElevate: false);
            return;
        }

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            await WriteDiagnosticsAsync();
            return;
        }

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Uninstall();
            MessageBox.Show("Discord WARP Watcher was removed from startup.", "Discord WARP Watcher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!IsRunningFromInstalledPath())
        {
            InstallSelf();
            var warpInstallAttempted = await EnsureWarpInstalledForSetupAsync();
            StartInstalled();
            ShowInstallResult(warpInstallAttempted);
            return;
        }

        InstallStartupEntries();

        using var mutex = new Mutex(true, @"Local\DiscordWarpWatcher", out var ownsMutex);
        if (!ownsMutex)
        {
            return;
        }

        try
        {
            await RunWatcherAsync();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
        }
    }

    private static async Task RunWatcherAsync()
    {
        StopOldHelpers();

        var lastWarpCheck = DateTime.MinValue;
        var lastPatchCheck = DateTime.MinValue;
        var lastRestart = DateTime.MinValue;

        while (true)
        {
            var now = DateTime.UtcNow;

            if (now - lastWarpCheck > TimeSpan.FromSeconds(25))
            {
                await EnsureWarpProxyAsync();
                lastWarpCheck = now;
            }

            var mainProcesses = GetDiscordMainProcesses();
            if (mainProcesses.Count == 0 && now - lastPatchCheck > TimeSpan.FromMinutes(1))
            {
                PatchLatestDiscordAsar();
                lastPatchCheck = now;
            }

            if (mainProcesses.Any(process => !process.IsProxied) &&
                now - lastRestart > TimeSpan.FromSeconds(4))
            {
                Log("Unproxied Discord detected. Restarting through WARP proxy.");
                if (!await EnsureWarpProxyAsync())
                {
                    Log("WARP proxy is not ready. Leaving the current Discord process untouched.");
                    lastRestart = now;
                    continue;
                }

                KillDiscord();
                await Task.Delay(700);
                PatchLatestDiscordAsar();
                LaunchDiscordWithProxy();
                lastRestart = now;
            }

            await Task.Delay(1000);
        }
    }

    private static async Task<bool> EnsureWarpProxyAsync()
    {
        var warpCli = FindWarpCli();
        if (warpCli is null)
        {
            Log("Cloudflare WARP is missing.");
            return false;
        }

        await EnsureWarpRegistrationAsync(warpCli);

        var selectedPort = await ResolveWarpProxyPortAsync(warpCli);
        await ConfigureWarpProxyAsync(warpCli, selectedPort);

        if (await WaitForSocks4ProxyAsync(selectedPort, TimeSpan.FromSeconds(10)))
        {
            CurrentWarpSocksPort = selectedPort;
            return true;
        }

        Log($"WARP proxy did not respond on port {selectedPort}. Trying a fallback port.");
        var fallbackPort = FindAvailableProxyPort(exceptPort: selectedPort);
        if (fallbackPort is null)
        {
            Log("No available fallback port was found for WARP proxy mode.");
            return false;
        }

        await ConfigureWarpProxyAsync(warpCli, fallbackPort.Value);
        if (await WaitForSocks4ProxyAsync(fallbackPort.Value, TimeSpan.FromSeconds(10)))
        {
            CurrentWarpSocksPort = fallbackPort.Value;
            Log($"WARP proxy fallback port selected: {fallbackPort.Value}");
            return true;
        }

        Log($"WARP proxy did not respond on fallback port {fallbackPort.Value}.");
        return false;
    }

    private static async Task ConfigureWarpProxyAsync(string warpCli, int port)
    {
        await RunWarpAsync(warpCli, "mode", TimeSpan.FromSeconds(8), "proxy");
        await RunWarpAsync(warpCli, "proxy", TimeSpan.FromSeconds(8), "port", port.ToString());
        await RunWarpAsync(warpCli, "connect", TimeSpan.FromSeconds(8));
    }

    private static async Task<int> ResolveWarpProxyPortAsync(string warpCli)
    {
        if (await IsSocks4ProxyReachableAsync(CurrentWarpSocksPort, TimeSpan.FromMilliseconds(1200)))
        {
            return CurrentWarpSocksPort;
        }

        var configuredPort = await GetConfiguredWarpProxyPortAsync(warpCli);
        if (configuredPort is not null &&
            configuredPort.Value is >= FallbackWarpSocksPortStart and <= FallbackWarpSocksPortEnd or PreferredWarpSocksPort)
        {
            if (await IsSocks4ProxyReachableAsync(configuredPort.Value, TimeSpan.FromMilliseconds(1200)) ||
                IsTcpPortAvailable(configuredPort.Value))
            {
                return configuredPort.Value;
            }

            Log($"Configured WARP proxy port {configuredPort.Value} is not usable. Another application may be using it.");
        }

        if (IsTcpPortAvailable(PreferredWarpSocksPort))
        {
            return PreferredWarpSocksPort;
        }

        Log($"Preferred WARP proxy port {PreferredWarpSocksPort} is busy. Looking for a fallback port.");
        return FindAvailableProxyPort(exceptPort: null) ?? PreferredWarpSocksPort;
    }

    private static async Task<int?> GetConfiguredWarpProxyPortAsync(string warpCli)
    {
        var result = await RunWarpCaptureAsync(warpCli, "settings", TimeSpan.FromSeconds(8));
        var match = Regex.Match(result.Output, @"Mode:\s*WarpProxy on port\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
        {
            return port;
        }

        return null;
    }

    private static int? FindAvailableProxyPort(int? exceptPort)
    {
        for (var port = FallbackWarpSocksPortStart; port <= FallbackWarpSocksPortEnd; port++)
        {
            if (exceptPort == port)
            {
                continue;
            }

            if (IsTcpPortAvailable(port))
            {
                return port;
            }
        }

        return null;
    }

    private static bool IsTcpPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static async Task<bool> WaitForSocks4ProxyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsSocks4ProxyReachableAsync(port, TimeSpan.FromMilliseconds(1500)))
            {
                return true;
            }

            await Task.Delay(750);
        }

        return false;
    }

    private static async Task<bool> IsSocks4ProxyReachableAsync(int port, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);

            var address = (await Dns.GetHostAddressesAsync("discord.com", cts.Token))
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            const int targetPort = 443;
            var request = new byte[]
            {
                0x04, 0x01, (byte)((targetPort >> 8) & 0xff), (byte)(targetPort & 0xff),
                bytes[0], bytes[1], bytes[2], bytes[3], 0x00
            };

            var stream = client.GetStream();
            await stream.WriteAsync(request, cts.Token);
            var response = new byte[8];
            var read = await stream.ReadAsync(response, cts.Token);
            return read >= 2 && response[1] == 0x5a;
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnsureWarpRegistrationAsync(string warpCli)
    {
        if (await IsWarpRegisteredAsync(warpCli))
        {
            return;
        }

        var result = await RunWarpCaptureAsync(warpCli, "registration", TimeSpan.FromSeconds(30), "new");
        if (result.ExitCode != 0)
        {
            Log("WARP registration failed. The network may block Cloudflare registration or the WARP client may need manual onboarding.");
            TryLaunchWarpUi();
        }
    }

    private static async Task<bool> IsWarpRegisteredAsync(string warpCli)
    {
        var result = await RunWarpCaptureAsync(warpCli, "registration", TimeSpan.FromSeconds(8), "show");
        if (result.ExitCode != 0)
        {
            return false;
        }

        return !result.Output.Contains("No registration", StringComparison.OrdinalIgnoreCase) &&
               !result.Output.Contains("Missing registration", StringComparison.OrdinalIgnoreCase) &&
               !result.Output.Contains("not registered", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryLaunchWarpUi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP", "Cloudflare WARP.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Cloudflare", "Cloudflare WARP", "Cloudflare WARP.exe")
        };

        var uiPath = candidates.FirstOrDefault(File.Exists);
        if (uiPath is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uiPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("Could not launch Cloudflare WARP UI: " + ex.Message);
        }
    }

    private static async Task RunWarpAsync(string warpCli, string command, TimeSpan timeout, params string[] arguments)
    {
        _ = await RunWarpCaptureAsync(warpCli, command, timeout, arguments);
    }

    private static async Task<CommandResult> RunWarpCaptureAsync(string warpCli, string command, TimeSpan timeout, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = warpCli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("--accept-tos");
        psi.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null) return new CommandResult(-1, "", "Process could not be started.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
        }

        var output = "";
        var error = "";
        var exitCode = process.HasExited ? process.ExitCode : -1;
        try
        {
            output = await outputTask;
            error = await errorTask;
            if (exitCode != 0 || !string.IsNullOrWhiteSpace(error))
            {
                Log($"warp-cli {command} exited {exitCode}: {output} {error}".Trim());
            }
        }
        catch { }

        return new CommandResult(exitCode, output, error);
    }

    private static IReadOnlyList<DiscordProcess> GetDiscordMainProcesses()
    {
        var result = new List<DiscordProcess>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'Discord.exe'");

            foreach (ManagementObject item in searcher.Get())
            {
                var pid = Convert.ToInt32(item["ProcessId"]);
                var commandLine = Convert.ToString(item["CommandLine"]) ?? "";

                if (commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new DiscordProcess(
                    pid,
                    IsDiscordProxied(commandLine)));
            }
        }
        catch (Exception ex)
        {
            Log("Could not query Discord processes: " + ex.Message);
        }

        return result;
    }

    private static bool IsDiscordProxied(string commandLine)
    {
        return commandLine.Contains($"--proxy-server={ProxyScheme}://127.0.0.1:{CurrentWarpSocksPort}", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains($"--proxy-server={ProxyScheme}://localhost:{CurrentWarpSocksPort}", StringComparison.OrdinalIgnoreCase);
    }

    private static void LaunchDiscordWithProxy()
    {
        var discordExe = FindDiscordExe();
        if (discordExe is null)
        {
            Log("Discord.exe not found.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = discordExe,
            WorkingDirectory = Path.GetDirectoryName(discordExe) ?? "",
            Arguments = $"--proxy-server={ProxyScheme}://127.0.0.1:{CurrentWarpSocksPort} --force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            UseShellExecute = true
        });
    }

    private static void KillDiscord()
    {
        foreach (var process in Process.GetProcessesByName("Discord"))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void StopOldHelpers()
    {
        var currentProcessId = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("DiscordWarpLauncher")
                     .Concat(Process.GetProcessesByName("DiscordWarpWatcher")))
        {
            try
            {
                if (process.Id != currentProcessId)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'");

            foreach (ManagementObject item in searcher.Get())
            {
                var commandLine = Convert.ToString(item["CommandLine"]) ?? "";
                if (!commandLine.Contains("discord-warp-http-bridge.js", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pid = Convert.ToInt32(item["ProcessId"]);
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private static void PatchLatestDiscordAsar()
    {
        try
        {
            var discordExe = FindDiscordExe();
            if (discordExe is null) return;

            var appDir = Path.GetDirectoryName(discordExe);
            if (string.IsNullOrWhiteSpace(appDir)) return;

            var asar = Path.Combine(appDir, "resources", "app.asar");
            PatchDiscordAsarFile(asar, allowElevate: true);
        }
        catch (Exception ex)
        {
            Log("PatchLatestDiscordAsar failed: " + ex.Message);
        }
    }

    private static void PatchDiscordAsarFile(string asar, bool allowElevate)
    {
        try
        {
            if (!File.Exists(asar))
            {
                Log($"Discord app.asar was not found at: {asar}");
                return;
            }

            var bytes = File.ReadAllBytes(asar);
            if (IndexOf(bytes, Encoding.ASCII.GetBytes(PatchMarker), 0, bytes.Length) >= 0)
            {
                return;
            }

            var functionNeedle = Encoding.ASCII.GetBytes("async function updateUntilCurrent(){");
            var functionStart = IndexOf(bytes, functionNeedle, 0, bytes.Length);
            if (functionStart < 0)
            {
                Log("Discord app.asar patch target was not found. Discord may have changed its startup updater code.");
                return;
            }

            var bodyStart = functionStart + functionNeedle.Length;
            var loopNeedle = Encoding.ASCII.GetBytes("for(;;){");
            var loopStart = IndexOf(bytes, loopNeedle, bodyStart, Math.Min(bytes.Length, bodyStart + 3000));
            if (loopStart < 0)
            {
                Log("Discord app.asar patch boundary was not found. Discord may have changed its startup updater code.");
                return;
            }

            var patch =
                "try{await newUpdater.startCurrentVersion({}),newUpdater.setRunningInBackground(),newUpdater.collectGarbage(),launchMainWindow(),updateSplashState(LAUNCHING);return}catch(e){}/*" +
                PatchMarker +
                "*/";

            var patchBytes = Encoding.ASCII.GetBytes(patch);
            var patchRoom = loopStart - bodyStart;
            if (patchBytes.Length > patchRoom)
            {
                Log("Discord app.asar patch room was too small.");
                return;
            }

            var backup = asar + ".discord-warp-backup";
            if (!File.Exists(backup))
            {
                File.Copy(asar, backup, overwrite: false);
            }

            Array.Fill<byte>(bytes, 0x20, bodyStart, patchRoom);
            Array.Copy(patchBytes, 0, bytes, bodyStart, patchBytes.Length);
            File.WriteAllBytes(asar, bytes);
            Log("Patched Discord app.asar for current-version startup.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log("Discord app.asar patch needs elevated permission: " + ex.Message);
            if (allowElevate)
            {
                TryPatchAsarElevated(asar);
            }
        }
        catch (IOException ex)
        {
            Log("Discord app.asar patch failed because the file is busy or unavailable: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log("PatchDiscordAsarFile failed: " + ex.Message);
        }
    }

    private static void TryPatchAsarElevated(string asar)
    {
        try
        {
            var exe = IsRunningFromInstalledPath()
                ? InstalledExe
                : Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Log("Could not request elevated app.asar patch because watcher executable was not found.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--patch-asar \"{asar}\"",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            Log("Could not request elevated app.asar patch: " + ex.Message);
        }
    }

    private static string? FindWarpCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP", "warp-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Cloudflare", "Cloudflare WARP", "warp-cli.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<bool> EnsureWarpInstalledForSetupAsync()
    {
        if (FindWarpCli() is not null)
        {
            return false;
        }

        var choice = MessageBox.Show(
            "Cloudflare WARP was not found. The official Cloudflare Windows installer will be downloaded and launched now. If Windows asks for administrator permission, allow it.",
            "Discord WARP Watcher",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (choice != DialogResult.OK)
        {
            return false;
        }

        try
        {
            var installerPath = Path.Combine(InstallDir, WarpInstallerName);
            await DownloadWarpInstallerAsync(installerPath);
            await RunWarpInstallerAsync(installerPath);

            if (await WaitForWarpCliAsync(TimeSpan.FromMinutes(3)))
            {
                return true;
            }

            MessageBox.Show(
                "WARP setup was started, but warp-cli was not found yet. After the installer finishes, run this setup file once more.",
                "Discord WARP Watcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return true;
        }
        catch (Exception ex)
        {
            Log("WARP install failed: " + ex);
            MessageBox.Show(
                "Automatic WARP installation could not be completed. The Cloudflare download page will open; install WARP, then run this setup file again.\n\n" + ex.Message,
                "Discord WARP Watcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            Process.Start(new ProcessStartInfo("https://one.one.one.one/") { UseShellExecute = true });
            return true;
        }
    }

    private static async Task DownloadWarpInstallerAsync(string installerPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath) ?? InstallDir);

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        using var response = await http.GetAsync(WarpDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output);
    }

    private static async Task RunWarpInstallerAsync(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{installerPath}\" /passive /norestart",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Windows Installer could not be started.");
        }

        await process.WaitForExitAsync();
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException($"WARP setup exited with code {process.ExitCode}.");
        }
    }

    private static async Task<bool> WaitForWarpCliAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindWarpCli() is not null)
            {
                return true;
            }

            await Task.Delay(1500);
        }

        return false;
    }

    private static string? FindDiscordExe()
    {
        return EnumerateDiscordExeCandidates()
            .Where(File.Exists)
            .Select(path => new DiscordInstall(path, ParseDiscordVersion(Path.GetDirectoryName(path) ?? ""), File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.LastWriteUtc)
            .Select(item => item.Path)
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> EnumerateDiscordExeCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateDiscordRoots())
        {
            foreach (var candidate in EnumerateDiscordExeCandidatesFromRoot(root))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var runningPath in EnumerateRunningDiscordExecutablePaths())
        {
            if (seen.Add(runningPath))
            {
                yield return runningPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateDiscordExeCandidatesFromRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var direct = Path.Combine(root, "Discord.exe");
        if (File.Exists(direct))
        {
            yield return direct;
        }

        IEnumerable<string> appDirectories;
        try
        {
            appDirectories = Directory.EnumerateDirectories(root, "app-*");
        }
        catch
        {
            yield break;
        }

        foreach (var appDirectory in appDirectories)
        {
            var exe = Path.Combine(appDirectory, "Discord.exe");
            if (File.Exists(exe))
            {
                yield return exe;
            }
        }
    }

    private static IEnumerable<string> EnumerateDiscordRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    path = Path.GetDirectoryName(path);
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var directoryName = Path.GetFileName(path);
                if (directoryName.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                {
                    path = Path.GetDirectoryName(path);
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    roots.Add(Path.GetFullPath(path));
                }
            }
            catch { }
        }

        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordCanary"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordPTB"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Discord"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Discord"));

        foreach (var path in EnumerateDiscordRegistryPaths())
        {
            AddRoot(path);
        }

        foreach (var root in roots)
        {
            yield return root;
        }
    }

    private static IEnumerable<string> EnumerateDiscordRegistryPaths()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath))
        {
            var runValue = Convert.ToString(run?.GetValue(DiscordRunValue));
            var executable = ExtractExecutablePath(runValue);
            if (executable is not null)
            {
                yield return executable;
            }
        }

        var uninstallKeys = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, view, keyPath) in uninstallKeys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(keyPath);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var subKey = uninstall.OpenSubKey(subKeyName);
                var displayName = Convert.ToString(subKey?.GetValue("DisplayName")) ?? "";
                if (!displayName.Contains("Discord", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var valueName in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
                {
                    var value = Convert.ToString(subKey?.GetValue(valueName));
                    var path = valueName.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase)
                        ? value
                        : ExtractExecutablePath(value);

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateRunningDiscordExecutablePaths()
    {
        var paths = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ExecutablePath FROM Win32_Process WHERE Name = 'Discord.exe'");

            foreach (ManagementObject item in searcher.Get())
            {
                var path = Convert.ToString(item["ExecutablePath"]);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Could not query running Discord executable paths: " + ex.Message);
        }

        foreach (var path in paths)
        {
            yield return path;
        }
    }

    private static Version ParseDiscordVersion(string appPath)
    {
        var name = Path.GetFileName(appPath);
        if (name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(name[4..], out var version))
        {
            return version;
        }

        return new Version(0, 0);
    }

    private static string? FindDiscordUpdateExe()
    {
        foreach (var registryPath in EnumerateDiscordRegistryPaths())
        {
            var fileName = Path.GetFileName(registryPath);
            if (fileName.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(registryPath))
            {
                return registryPath;
            }
        }

        foreach (var root in EnumerateDiscordRoots())
        {
            var updateExe = Path.Combine(root, "Update.exe");
            if (File.Exists(updateExe))
            {
                return updateExe;
            }
        }

        return null;
    }

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            return endQuote > 1 ? value[1..endQuote] : null;
        }

        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex < 0)
        {
            return value.Trim().TrimEnd(',');
        }

        return value[..(exeIndex + 4)].Trim();
    }

    private static void InstallSelf()
    {
        Directory.CreateDirectory(InstallDir);
        var current = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(current)) throw new InvalidOperationException("The running executable path could not be found.");

        StopOldHelpers();
        File.Copy(current, InstalledExe, overwrite: true);
        InstallStartupEntries();
    }

    private static void InstallStartupEntries()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        run.DeleteValue(OldLauncherRunValue, throwOnMissingValue: false);
        run.SetValue(WatcherRunValue, $"\"{InstalledExe}\"");

        var updateExe = FindDiscordUpdateExe();
        if (updateExe is not null)
        {
            run.SetValue(DiscordRunValue, $"\"{updateExe}\" --processStart Discord.exe");
            RestoreDesktopDiscordShortcut(updateExe);
        }

        DeleteExtraShortcut();
    }

    private static void RestoreDesktopDiscordShortcut(string updateExe)
    {
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Discord.lnk");

        if (!File.Exists(desktopShortcut))
        {
            return;
        }

        CreateShortcut(desktopShortcut, updateExe, "--processStart Discord.exe", "Discord", FindDiscordExe());
    }

    private static void DeleteExtraShortcut()
    {
        var extraShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Discord WARP.lnk");

        try
        {
            if (File.Exists(extraShortcut))
            {
                File.Delete(extraShortcut);
            }
        }
        catch (Exception ex)
        {
            Log("Could not delete extra shortcut: " + ex.Message);
        }
    }

    private static void Uninstall()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        run?.DeleteValue(WatcherRunValue, throwOnMissingValue: false);
        run?.DeleteValue(OldLauncherRunValue, throwOnMissingValue: false);
    }

    private static bool IsRunningFromInstalledPath()
    {
        var current = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        return string.Equals(Path.GetFullPath(current), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
    }

    private static void StartInstalled()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = InstalledExe,
            UseShellExecute = true
        });
    }

    private static void ShowInstallResult(bool warpInstallAttempted)
    {
        var missing = new List<string>();
        if (FindWarpCli() is null) missing.Add("Cloudflare WARP");
        if (FindDiscordExe() is null) missing.Add("Discord");

        if (missing.Count == 0)
        {
            MessageBox.Show(
                warpInstallAttempted
                    ? "Setup is complete. Cloudflare WARP was installed too. From now on, Discord will be detected and relaunched through the local WARP proxy when needed."
                    : "Setup is complete. From now on, Discord will be detected and relaunched through the local WARP proxy when needed.",
                "Discord WARP Watcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        MessageBox.Show(
            "Setup is installed, but these requirements are still missing: " + string.Join(", ", missing) + ". Install them, then open Discord normally.",
            "Discord WARP Watcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        if (FindWarpCli() is null)
        {
            Process.Start(new ProcessStartInfo("https://one.one.one.one/") { UseShellExecute = true });
        }

        if (FindDiscordExe() is null)
        {
            Process.Start(new ProcessStartInfo("https://discord.com/download") { UseShellExecute = true });
        }
    }

    private static async Task WriteDiagnosticsAsync()
    {
        var diagnosticsPath = Path.Combine(InstallDir, "diagnostics.txt");
        var builder = new StringBuilder();
        builder.AppendLine("Discord WARP Watcher Diagnostics");
        builder.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("OS: " + Environment.OSVersion);
        builder.AppendLine("64-bit OS: " + Environment.Is64BitOperatingSystem);
        builder.AppendLine();

        var warpCli = FindWarpCli();
        builder.AppendLine("[Cloudflare WARP]");
        builder.AppendLine("warp-cli: " + (warpCli ?? "not found"));
        if (warpCli is not null)
        {
            builder.AppendLine("Preferred port available: " + IsTcpPortAvailable(PreferredWarpSocksPort));
            builder.AppendLine("Preferred port SOCKS4 reachable: " + await IsSocks4ProxyReachableAsync(PreferredWarpSocksPort, TimeSpan.FromMilliseconds(1500)));
            var configuredPort = await GetConfiguredWarpProxyPortAsync(warpCli);
            builder.AppendLine("Configured proxy port: " + (configuredPort?.ToString() ?? "unknown"));
            builder.AppendLine();
            builder.AppendLine("$ warp-cli --version");
            builder.AppendLine((await RunWarpCaptureAsync(warpCli, "--version", TimeSpan.FromSeconds(8))).Output);
            builder.AppendLine("$ warp-cli status");
            builder.AppendLine((await RunWarpCaptureAsync(warpCli, "status", TimeSpan.FromSeconds(8))).Output);
            builder.AppendLine("$ warp-cli settings");
            builder.AppendLine((await RunWarpCaptureAsync(warpCli, "settings", TimeSpan.FromSeconds(8))).Output);
        }

        builder.AppendLine();
        builder.AppendLine("[Discord]");
        var discordExe = FindDiscordExe();
        var updateExe = FindDiscordUpdateExe();
        builder.AppendLine("Discord.exe: " + (discordExe ?? "not found"));
        builder.AppendLine("Update.exe: " + (updateExe ?? "not found"));
        if (discordExe is not null)
        {
            var appDir = Path.GetDirectoryName(discordExe);
            var asar = appDir is null ? null : Path.Combine(appDir, "resources", "app.asar");
            builder.AppendLine("app.asar: " + (asar ?? "unknown"));
            builder.AppendLine("app.asar exists: " + (asar is not null && File.Exists(asar)));
        }

        builder.AppendLine();
        builder.AppendLine("[Startup]");
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath))
        {
            builder.AppendLine("Discord: " + Convert.ToString(run?.GetValue(DiscordRunValue)));
            builder.AppendLine("DiscordWarpWatcher: " + Convert.ToString(run?.GetValue(WatcherRunValue)));
            builder.AppendLine("DiscordWarp: " + Convert.ToString(run?.GetValue(OldLauncherRunValue)));
        }

        builder.AppendLine();
        builder.AppendLine("[Discord processes]");
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'Discord.exe'");
            foreach (ManagementObject item in searcher.Get())
            {
                builder.AppendLine("PID: " + Convert.ToString(item["ProcessId"]));
                builder.AppendLine("Path: " + Convert.ToString(item["ExecutablePath"]));
                builder.AppendLine("CommandLine: " + Convert.ToString(item["CommandLine"]));
                builder.AppendLine();
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine("Could not query Discord processes: " + ex.Message);
        }

        File.WriteAllText(diagnosticsPath, builder.ToString());
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{diagnosticsPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string description, string? iconPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
            shortcut.Description = description;
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                shortcut.IconLocation = iconPath;
            }

            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Log("Shortcut creation failed: " + ex.Message);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        if (needle.Length == 0) return start;

        end = Math.Min(end, haystack.Length);
        for (var i = start; i <= end - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found) return i;
        }

        return -1;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}

internal sealed record DiscordProcess(int ProcessId, bool IsProxied);

internal sealed record DiscordInstall(string Path, Version Version, DateTime LastWriteUtc);

internal sealed record CommandResult(int ExitCode, string Output, string Error);
