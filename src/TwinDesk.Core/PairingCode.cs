using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwinDesk.Core;

public sealed record PairingCode(int Version, string ComputerName, string Fingerprint, string Address, int Port, string Platform)
{
    public string Encode() => "TD1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static PairingCode Decode(string text)
    {
        if (text is null || text.Length > 4096 || !text.StartsWith("TD1.", StringComparison.Ordinal))
            throw new FormatException("配对信息格式不正确，请从另一台电脑复制完整的 TD1. 配对信息。");
        try
        {
            var body = text[4..].Trim().Replace('-', '+').Replace('_', '/');
            body = body.PadRight((body.Length + 3) / 4 * 4, '=');
            var value = JsonSerializer.Deserialize<PairingCode>(Convert.FromBase64String(body))
                        ?? throw new FormatException();
            if (value.Version != 1 || !AppSettings.IsScreenName(value.ComputerName) ||
                value.Port is < 1 or > 65535 || value.Address is null || value.Address.Length > 253 ||
                value.Platform is null || value.Platform.Length > 64 ||
                !Regex.IsMatch(value.Fingerprint ?? "", @"\A(?:[A-Fa-f0-9]{64}|(?:[A-Fa-f0-9]{2}:){31}[A-Fa-f0-9]{2})\z"))
                throw new FormatException();
            if (value.Address.Length != 0 && Uri.CheckHostName(value.Address.Trim('[', ']')) == UriHostNameType.Unknown)
                throw new FormatException();
            return value;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw new FormatException("配对信息已损坏或版本不支持，请重新复制。", e);
        }
    }

    public static string SuggestedAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."))
                .Select(a => a.ToString()).FirstOrDefault() ?? "";
        }
        catch (NetworkInformationException) { return ""; }
    }

    public static string CurrentPlatform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
}
