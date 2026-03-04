using System;
using System.IO;
using System.Text.Json;
using VideoWallpaper.App.Models;

namespace VideoWallpaper.App.Infrastructure;

public static class SettingsStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoWallpaper");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static string FilePath => SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                AppLogger.Info("Settings: no existing settings file; using defaults.");
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            AppLogger.Info($"Settings: loaded from {SettingsPath}");
            return loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Settings: failed to load settings; using defaults.", ex);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsDirectory);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsPath, json);
            }

            AppLogger.Info($"Settings: saved to {SettingsPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Settings: failed to save settings.", ex);
        }
    }
}
