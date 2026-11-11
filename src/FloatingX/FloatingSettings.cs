using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FloatingX;

internal sealed class FloatingSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasiPlugin.FloatingX", "settings.json");

    internal static FloatingSettings Current { get; private set; } = Load();

    public List<string> ButtonOrder { get; set; } = new();

    public List<string> HiddenButtons { get; set; } = new();

    internal static void Save(IEnumerable<string> order, IEnumerable<string> hiddenButtons)
    {
        var settings = new FloatingSettings
        {
            ButtonOrder = Normalize(order),
            HiddenButtons = Normalize(hiddenButtons)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, FilePath, overwrite: true);
            Current = settings;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FloatingSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<FloatingSettings>(File.ReadAllText(FilePath));
                if (settings != null)
                {
                    settings.ButtonOrder = Normalize(settings.ButtonOrder);
                    settings.HiddenButtons = Normalize(settings.HiddenButtons);
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("无法读取悬浮窗设置，将使用默认值：{0}", ex);
        }

        return new FloatingSettings();
    }

    private static List<string> Normalize(IEnumerable<string>? keys) =>
        (keys ?? Array.Empty<string>()).Where(key => !string.IsNullOrWhiteSpace(key))
        .Distinct(StringComparer.Ordinal).ToList();
}
