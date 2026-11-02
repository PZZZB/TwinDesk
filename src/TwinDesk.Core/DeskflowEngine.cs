using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TwinDesk.Core;

/// <summary>Runs one owned Deskflow 1.27.0 core process with an isolated TwinDesk profile.</summary>
public sealed class DeskflowEngine : IDisposable
{
    private readonly string _profileDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task _completion = Task.CompletedTask;
    private bool _disposed;

    public DeskflowEngine(string profileDir) => _profileDir = Path.GetFullPath(profileDir);

    public event Action<string>? Log;
    public event Action<int>? Exited;
    public bool IsRunning
    {
        get
        {
            var process = Volatile.Read(ref _process);
            try { return process is not null && !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedOwnedProcess = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) throw new InvalidOperationException("共享已在运行，请先停止再修改设置。");
            // Finish the previous exit callback and pipe readers before installing another lifecycle.
            await _completion.ConfigureAwait(false);
            var errors = settings.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(settings));
            var binary = string.IsNullOrWhiteSpace(settings.EnginePath) ? EngineDiscovery.Find() : Path.GetFullPath(settings.EnginePath);
            if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
                throw new FileNotFoundException("找不到 Deskflow 1.27.0 引擎，请在高级设置中选择 deskflow-core。");
            if (OperatingSystem.IsWindows() && !EngineDiscovery.IsWindowsPortable(binary))
                throw new InvalidOperationException("请选择 Deskflow 官方便携版中的 deskflow-core.exe。普通安装版会使用共享配置目录，本程序不会修改它。");

            var identity = new IdentityStore(_profileDir);
            identity.EnsureIdentity();
            if (identity.GetTrustedPeer(settings.Role) is null)
                throw new InvalidOperationException("请先互相复制两台电脑的本机指纹，并保存对方指纹完成配对。");
            var settingsFile = Path.Combine(_profileDir, "deskflow.ini");
            IdentityStore.WriteAtomic(settingsFile, GenerateSettings(settings, _profileDir));
            IdentityStore.WriteAtomic(Path.Combine(_profileDir, "layout.conf"), ServerConfig.Generate(settings));

            await CheckVersionAsync(binary, settingsFile, cancellationToken).ConfigureAwait(false);
            var start = CreateStartInfo(binary);
            start.ArgumentList.Add(settings.Role == ComputerRole.Server ? "server" : "client");
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(settingsFile);
            var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("引擎未能启动。");
            }
            catch { process.Dispose(); throw; }
            _process = process;
            startedOwnedProcess = true;
            _completion = ObserveAsync(process);
            EmitLog(settings.Role == ComputerRole.Server ? "引擎已启动，等待另一台电脑连接。" : "引擎已启动，正在连接控制电脑。");
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            if (!IsRunning) throw new InvalidOperationException("引擎启动后退出，请查看运行日志。");
        }
        catch
        {
            if (startedOwnedProcess)
            {
                try { await StopOwnedProcessAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) { EmitLog($"引擎启动失败后的清理异常：{cleanupError.Message}"); }
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopOwnedProcessAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Generates the actual Deskflow 1.27 settings, including computer definitions moved out of layout.conf.</summary>
    public static string GenerateSettings(AppSettings settings, string profileDir)
    {
        var identity = new IdentityStore(profileDir);
        var output = new StringBuilder();
        output.AppendLine("[core]");
        output.AppendLine($"computerName={IniString(settings.LocalName)}");
        output.AppendLine($"port={settings.Port.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"interface={IniString(settings.Role == ComputerRole.Server ? NativeHost(settings.ListenAddress) : "")}");
        output.AppendLine("processMode=0");
        output.AppendLine("preventSleep=false");
        output.AppendLine("\n[client]");
        output.AppendLine($"remoteHost={IniString(NativeHost(settings.PeerHost))}");
        output.AppendLine("dynamicConnectionInterval=true");
        output.AppendLine("\n[server]");
        output.AppendLine("externalConfig=true");
        output.AppendLine($"externalConfigFile={IniString(Path.Combine(Path.GetFullPath(profileDir), "layout.conf"))}");
        output.AppendLine($"enableClipboard={Boolean(settings.ClipboardSharing)}");
        output.AppendLine("clipboardSize=3");
        output.AppendLine("enableHeartbeat=true");
        output.AppendLine("heartbeat=5000");
        output.AppendLine($"enableSwitchDelay={Boolean(settings.SwitchDelayMs > 0)}");
        output.AppendLine($"switchDelay={settings.SwitchDelayMs.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine("enableSwitchDoubleTap=false");
        output.AppendLine("protocol=barrier");
        output.AppendLine("\n[security]");
        output.AppendLine("tlsEnabled=true");
        output.AppendLine("checkPeerFingerprints=true");
        output.AppendLine("keySize=2048");
        output.AppendLine($"certificate={IniString(identity.CertificatePath)}");
        output.AppendLine("\n[log]\nlevel=INFO\ntoFile=false");
        output.AppendLine($"\n[computer_{settings.LocalName}]\nname={IniString(settings.LocalName)}");
        output.AppendLine($"\n[computer_{settings.PeerName}]\nname={IniString(settings.PeerName)}");
        return output.ToString();
    }

    private static string Boolean(bool value) => value ? "true" : "false";
    // Deskflow accepts raw IPv6 with a separate core/port, but rejects [IPv6] without :port.
    private static string NativeHost(string host) => host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
    private static string IniString(string value) => "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";

    private ProcessStartInfo CreateStartInfo(string binary)
    {
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binary)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Isolate the native engine's initial lookup before --settings is processed.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(_profileDir, "native-config");
            start.Environment["XDG_STATE_HOME"] = Path.Combine(_profileDir, "native-state");
        }
        return start;
    }

    private async Task CheckVersionAsync(string binary, string settingsFile, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(binary);
        info.ArgumentList.Add("server");
        info.ArgumentList.Add("--settings");
        info.ArgumentList.Add(settingsFile);
        info.ArgumentList.Add("--version");
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var version = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || !Regex.IsMatch(version, @"\bv?1\.27\.0(?:\s|\.|\(|,|$)"))
            throw new InvalidOperationException($"此版本仅支持 Deskflow {EngineDiscovery.SupportedVersion}，请选择对应引擎。");
    }

    private async Task ObserveAsync(Process process)
    {
        var stdout = ReadLinesAsync(process.StandardOutput);
        var stderr = ReadLinesAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            Interlocked.CompareExchange(ref _process, null, process);
            EmitLog($"共享已停止（退出码 {exitCode}）。");
            try { Exited?.Invoke(exitCode); } catch { /* A UI subscriber must not break process cleanup. */ }
        }
        finally { process.Dispose(); }
    }

    private async Task ReadLinesAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) EmitLog(line);
    }

    private async Task StopOwnedProcessAsync()
    {
        var completion = _completion;
        var process = Volatile.Read(ref _process);
        if (process is null) { await completion.ConfigureAwait(false); return; }
        try
        {
            if (!process.HasExited)
            {
                if (OperatingSystem.IsWindows()) process.CloseMainWindow();
                else KillSignal(process.Id, 15);
                var finished = await Task.WhenAny(completion, Task.Delay(800)).ConfigureAwait(false);
                if (finished != completion && !process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* It exited while stop was requested. */ }
        await completion.ConfigureAwait(false);
    }

    private void EmitLog(string line)
    {
        try { Log?.Invoke(line); } catch { /* A UI subscriber must not stop draining the engine pipes. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillSignal(int pid, int signal);
}
