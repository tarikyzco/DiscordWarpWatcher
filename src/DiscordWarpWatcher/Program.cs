using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

namespace DiscordWarpWatcher;

internal static class Program
{
    private const int WarpSocksPort = 40000;
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

    [STAThread]
    private static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(InstallDir);

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
                KillDiscord();
                await Task.Delay(700);
                await EnsureWarpProxyAsync();
                PatchLatestDiscordAsar();
                LaunchDiscordWithProxy();
                lastRestart = now;
            }

            await Task.Delay(1000);
        }
    }

    private static async Task EnsureWarpProxyAsync()
    {
        var warpCli = FindWarpCli();
        if (warpCli is null)
        {
            Log("Cloudflare WARP is missing.");
            return;
        }

        await EnsureWarpRegistrationAsync(warpCli);
        await RunWarpAsync(warpCli, "mode", TimeSpan.FromSeconds(8), "proxy");
        await RunWarpAsync(warpCli, "proxy", TimeSpan.FromSeconds(8), "port", WarpSocksPort.ToString());
        await RunWarpAsync(warpCli, "connect", TimeSpan.FromSeconds(8));
    }

    private static async Task EnsureWarpRegistrationAsync(string warpCli)
    {
        if (await IsWarpRegisteredAsync(warpCli))
        {
            return;
        }

        await RunWarpAsync(warpCli, "registration", TimeSpan.FromSeconds(20), "new");
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
                    commandLine.Contains($"--proxy-server={ProxyScheme}://127.0.0.1:{WarpSocksPort}", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex)
        {
            Log("Could not query Discord processes: " + ex.Message);
        }

        return result;
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
            Arguments = $"--proxy-server={ProxyScheme}://127.0.0.1:{WarpSocksPort} --force-webrtc-ip-handling-policy=disable_non_proxied_udp",
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
            if (!File.Exists(asar)) return;

            var bytes = File.ReadAllBytes(asar);
            if (IndexOf(bytes, Encoding.ASCII.GetBytes(PatchMarker), 0, bytes.Length) >= 0)
            {
                return;
            }

            var functionNeedle = Encoding.ASCII.GetBytes("async function updateUntilCurrent(){");
            var functionStart = IndexOf(bytes, functionNeedle, 0, bytes.Length);
            if (functionStart < 0)
            {
                Log("Discord app.asar patch target was not found.");
                return;
            }

            var bodyStart = functionStart + functionNeedle.Length;
            var loopNeedle = Encoding.ASCII.GetBytes("for(;;){");
            var loopStart = IndexOf(bytes, loopNeedle, bodyStart, Math.Min(bytes.Length, bodyStart + 3000));
            if (loopStart < 0)
            {
                Log("Discord app.asar patch boundary was not found.");
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
        catch (Exception ex)
        {
            Log("PatchLatestDiscordAsar failed: " + ex.Message);
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
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateDirectories(root, "app-*")
            .Select(path => new DiscordInstall(path, ParseDiscordVersion(path), Directory.GetLastWriteTimeUtc(path)))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.LastWriteUtc)
            .Select(item => Path.Combine(item.Path, "Discord.exe"))
            .FirstOrDefault(File.Exists);
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
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Discord", "Update.exe");
        return File.Exists(path) ? path : null;
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
