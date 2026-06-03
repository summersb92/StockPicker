using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Persists and restores the scan cache to/from disk so the app can show
    /// recommendations instantly on restart without waiting for a network fetch.
    ///
    /// Cache location: %LOCALAPPDATA%\StockPicker\scan_cache.json
    /// </summary>
    public class ScanCacheService
    {
        private static readonly string CacheFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockPicker");

        private static readonly string CacheFile =
            Path.Combine(CacheFolder, "scan_cache.json");

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented            = false,   // compact — file can be several MB
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Persists the current scan data to disk.
        /// Failures are silently swallowed — a missing cache is always recoverable.
        /// </summary>
        public async Task SaveAsync(ScanCache cache)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);

                // Write to a temp file then atomic-rename so a crash mid-write
                // doesn't corrupt the last-good cache.
                var tmp = CacheFile + ".tmp";
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 65536, useAsync: true);
                await JsonSerializer.SerializeAsync(fs, cache, _jsonOptions);
                await fs.FlushAsync();
                fs.Close();

                File.Move(tmp, CacheFile, overwrite: true);
            }
            catch
            {
                // Cache save is best-effort — never let it crash the app.
            }
        }

        /// <summary>
        /// Loads the persisted cache from disk.
        /// Returns <c>null</c> if the file does not exist, is corrupt, or cannot be read.
        /// </summary>
        public async Task<ScanCache?> LoadAsync()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;

                await using var fs = new FileStream(CacheFile, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read, 65536, useAsync: true);
                return await JsonSerializer.DeserializeAsync<ScanCache>(fs, _jsonOptions);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when a cache file exists on disk.
        /// Does not validate its contents.
        /// </summary>
        public bool Exists() => File.Exists(CacheFile);
    }
}
