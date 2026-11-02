using System.Net;
using System.Text.RegularExpressions;

namespace TwinDesk.Core;

public enum ComputerRole { Server, Client }
public enum NetworkMode { Lan, Internet }
public enum ScreenPosition { Left, Right, Above, Below }

/// <summary>Settings shared by the desktop UI and the native input engine.</summary>
public sealed record AppSettings
{
    public ComputerRole Role { get; init; } = ComputerRole.Server;
    public NetworkMode Network { get; init; } = NetworkMode.Lan;
    public string LocalName { get; init; } = SanitizeComputerName(Environment.MachineName);
    public string PeerName { get; init; } = "peer-computer";
    public string PeerHost { get; init; } = "";
    public string ListenAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 24800;
    public ScreenPosition Position { get; init; } = ScreenPosition.Right;
    public bool EdgeSwitching { get; init; } = true;
    public bool HotkeySwitching { get; init; } = true;
    public int SwitchDelayMs { get; init; } = 200;
    public bool ClipboardSharing { get; init; }
    public string EnginePath { get; init; } = "";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Role)) errors.Add("请选择有效的电脑角色。");
        if (!Enum.IsDefined(Network)) errors.Add("请选择有效的连接方式。");
        if (!Enum.IsDefined(Position)) errors.Add("请选择有效的屏幕位置。");
        if (!IsScreenName(LocalName)) errors.Add("本机名称需为 1–128 位英文字母、数字、点、横线或下划线，并至少包含一个字母或数字。");
        if (!IsScreenName(PeerName)) errors.Add("另一台电脑的名称需为 1–128 位英文字母、数字、点、横线或下划线，并至少包含一个字母或数字。");
        if (!string.IsNullOrEmpty(LocalName) && string.Equals(LocalName, PeerName, StringComparison.OrdinalIgnoreCase))
            errors.Add("两台电脑的名称不能相同，大小写不同也视为同名。");
        if (Port is < 1 or > 65535) errors.Add("端口需在 1–65535 之间。");
        if (SwitchDelayMs is < 0 or > 5000) errors.Add("边缘停留时间需在 0–5000 毫秒之间。");
        if (!EdgeSwitching && !HotkeySwitching) errors.Add("请至少启用边缘切换或快捷键切换中的一种。");
        if (PeerHost is null)
            errors.Add("对端地址不能为 null；尚未填写时请使用空字符串。");
        else if (Role == ComputerRole.Client && string.IsNullOrEmpty(PeerHost))
            errors.Add("请输入连接键鼠所在电脑的 IP 地址或主机名。");
        else if (!string.IsNullOrEmpty(PeerHost) && !IsHost(PeerHost))
            errors.Add("对端地址需为有效的主机名、IPv4 或 IPv6；请将端口填在端口栏。");
        if (string.IsNullOrEmpty(ListenAddress) || !TryParseAddress(ListenAddress, out _))
            errors.Add("监听地址需为有效的 IPv4 或 IPv6，例如 0.0.0.0 或 ::。");
        if (EnginePath is null || EnginePath.IndexOfAny(['\0', '\r', '\n']) >= 0)
            errors.Add("引擎路径不能包含换行或空字符。");
        return errors;
    }

    public static string SanitizeComputerName(string? name)
    {
        var cleaned = Regex.Replace(name ?? "", "[^A-Za-z0-9_.-]", "-");
        if (cleaned.Length > 128) cleaned = cleaned[..128];
        return IsScreenName(cleaned) ? cleaned : "this-computer";
    }

    internal static bool IsScreenName(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && Regex.IsMatch(value, "\\A[A-Za-z0-9_.-]+\\z", RegexOptions.CultureInvariant)
        && value.Any(char.IsAsciiLetterOrDigit);

    internal static bool TryParseAddress(string value, out IPAddress? address)
    {
        address = null;
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.IndexOfAny(['\0', '\r', '\n']) >= 0) return false;
        var candidate = value;
        if (candidate.StartsWith('[') && candidate.EndsWith(']')) candidate = candidate[1..^1];
        else if (candidate.Contains('[') || candidate.Contains(']')) return false;
        return IPAddress.TryParse(candidate, out address);
    }

    internal static bool IsHost(string value)
    {
        if (value.Length > 253 || value.Any(char.IsWhiteSpace)) return false;
        if (TryParseAddress(value, out _)) return true;
        var host = value.EndsWith('.') ? value[..^1] : value;
        if (host.Length == 0 || !Regex.IsMatch(host, "\\A[A-Za-z0-9.-]+\\z", RegexOptions.CultureInvariant)) return false;
        var labels = host.Split('.');
        if (labels.All(label => label.All(char.IsAsciiDigit))) return false;
        return labels.All(label => label.Length is >= 1 and <= 63
            && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]));
    }
}
