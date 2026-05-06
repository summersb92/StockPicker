using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Persists and restores <see cref="UserSettings"/> (column visibility + sort order)
    /// to/from %LOCALAPPDATA%\StockPicker\user_settings.json.
    /// </summary>
    public class UserSettingsService
    {
        private static readonly string CacheFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockPicker");

        private static readonly string SettingsFile =
            Path.Combine(CacheFolder, "user_settings.json");

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented           = true,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Synchronously loads settings from disk.
        /// Returns a fresh default instance if the file is missing or corrupt.
        /// Safe to call from a constructor — the file is tiny (&lt; 2 KB).
        /// </summary>
        public UserSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new UserSettings();
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions)
                       ?? new UserSettings();
            }
            catch
            {
                return new UserSettings();
            }
        }

        /// <summary>
        /// Asynchronously persists <paramref name="settings"/> to disk.
        /// Writes to a .tmp file first and renames atomically.
        /// Failures are silently swallowed.
        /// </summary>
        public async Task SaveAsync(UserSettings settings)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var tmp = SettingsFile + ".tmp";
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 4096, useAsync: true);
                await JsonSerializer.SerializeAsync(fs, settings, _jsonOptions);
                await fs.FlushAsync();
                fs.Close();
                File.Move(tmp, SettingsFile, overwrite: true);
            }
            catch { /* best-effort */ }
        }
    }
}
