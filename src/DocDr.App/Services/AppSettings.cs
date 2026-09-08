using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocDr.App.Services;

/// <summary>Small persisted user preferences at %APPDATA%\DocDr\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>How many recent files to keep.</summary>
    public const int MaxRecentFiles = 12;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocDr", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Last page size picked in the New-document dialog ("A4", "A3", … or "Custom").</summary>
    public string LastPageSize { get; set; } = "A4";

    /// <summary>Last custom page size (mm) entered in the New-document dialog.</summary>
    public double LastCustomWidthMm { get; set; } = 210;

    public double LastCustomHeightMm { get; set; } = 297;

    public bool LastPageLandscape { get; set; }

    /// <summary>The user's last-picked custom annotation colour (ARGB), for the "Custom" swatch.</summary>
    public uint LastCustomColor { get; set; } = 0xFF3F51B5;

    /// <summary>Absolute paths of recently opened documents, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Move <paramref name="path"/> to the front of the recent list (deduped, capped).</summary>
    public void PushRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through to defaults
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
