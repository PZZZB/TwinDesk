using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwinDesk.Core;

/// <summary>Persists settings without replacing a valid file on validation or write failure.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public SettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = Path.GetFullPath(directory);
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            if (new FileInfo(SettingsPath).Length > 1024 * 1024)
                throw new InvalidDataException("设置文件超过 1 MiB，请检查文件内容。");
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(SettingsPath), JsonOptions)
                ?? throw new InvalidDataException("设置文件内容为空。");
            var errors = settings.Validate();
            if (errors.Count != 0) throw new InvalidDataException("设置文件包含无效内容：" + string.Join("；", errors));
            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法读取设置文件 {SettingsPath}：JSON 格式或字段类型不正确。", ex);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(settings));
        var content = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { /* Preserve the original write error if cleanup fails. */ }
                catch (UnauthorizedAccessException) { /* Preserve the original write error if cleanup fails. */ }
            }
        }
    }
}
