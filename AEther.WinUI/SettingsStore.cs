#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AEther.WinUI;

/// <summary>Reads and writes the per-user settings file.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public SettingsStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AEther", "settings.json");

    public AppSettings Load()
    {
        FileStream stream;
        try { stream = File.OpenRead(FilePath); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        using (stream)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions)
                ?? throw new JsonException("Settings must be a JSON object.");
            if (settings.Analyzer is null || settings.Graphics is null)
                throw new JsonException("Settings sections cannot be null.");
            settings.Graphics.Validate();
            // The actual input format is validated when starting the analyzer.
            settings.Analyzer.Validate(new(SampleType.Float32.Instance, 192000, 2));
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
