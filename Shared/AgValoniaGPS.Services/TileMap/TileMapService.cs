// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.TileMap;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.TileMap;

/// <summary>
/// XYZ tile service with two-level cache (memory + disk).
/// Tile source and settings are read from <see cref="ConfigurationStore.Instance"/>.
/// </summary>
public class TileMapService : ITileMapService
{
    // ── Static singleton ────────────────────────────────────────────────────
    private static TileMapService? _instance;
    public static TileMapService? Instance => _instance;

    // ── Constants ───────────────────────────────────────────────────────────
    private const string OsmUrlTemplate  = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    private const int    MemCacheMax     = 512;   // max tiles kept in memory
    private const int    MaxConcurrent   = 4;     // parallel downloads

    // ── HTTP ────────────────────────────────────────────────────────────────
    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "AgValoniaGPS/1.0 (https://github.com/AgValoniaGPS; tile-client)" }
        },
        Timeout = TimeSpan.FromSeconds(10)
    };

    // ── Caches ───────────────────────────────────────────────────────────────
    // Memory cache: key → raw PNG bytes (we store bytes, not Bitmap, so it is
    // thread-safe and the caller can decode on the UI thread if desired)
    private readonly ConcurrentDictionary<string, byte[]> _memCache = new();

    // Pending downloads: set of keys currently being fetched so we don't duplicate
    private readonly ConcurrentDictionary<string, bool> _pending = new();

    // Callbacks waiting for a specific tile
    private readonly ConcurrentDictionary<string, List<Action>> _callbacks = new();
    private readonly object _callbackLock = new();

    // Semaphore to limit concurrent downloads
    private readonly SemaphoreSlim _downloadSem = new(MaxConcurrent, MaxConcurrent);

    // ── Disk cache root ──────────────────────────────────────────────────────
    private readonly string _cacheRoot;

    // ── Constructor ──────────────────────────────────────────────────────────
    public TileMapService()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgValoniaGPS", "tile_cache");

        // Register as singleton (DrawingContextMapControl accesses it statically)
        _instance = this;
    }

    // ── ITileMapService ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Stream? GetTile(int z, int x, int y, Action onLoaded)
    {
        string key = TileKey(z, x, y);

        // 1. Memory hit
        if (_memCache.TryGetValue(key, out var bytes))
            return new MemoryStream(bytes, writable: false);

        // 2. Disk hit
        string diskPath = DiskPath(z, x, y);
        if (File.Exists(diskPath))
        {
            try
            {
                bytes = File.ReadAllBytes(diskPath);
                PutMemCache(key, bytes);
                return new MemoryStream(bytes, writable: false);
            }
            catch { /* corrupted file – fall through to download */ }
        }

        // 3. Start async download (fire-and-forget)
        if (_pending.TryAdd(key, true))
        {
            RegisterCallback(key, onLoaded);
            _ = DownloadTileAsync(z, x, y, key, diskPath);
        }
        else
        {
            // Download already in progress – just register the callback
            RegisterCallback(key, onLoaded);
        }

        return null;
    }

    /// <inheritdoc/>
    public (int tileX, int tileY) LatLonToTile(double lat, double lon, int zoom)
    {
        int n = 1 << zoom;
        int x = (int)Math.Floor((lon + 180.0) / 360.0 * n);

        double latRad = lat * Math.PI / 180.0;
        int y = (int)Math.Floor(
            (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);

        return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }

    /// <inheritdoc/>
    public (double nwLat, double nwLon, double seLat, double seLon) TileBounds(int x, int y, int z)
    {
        int n = 1 << z;
        double nwLon = x / (double)n * 360.0 - 180.0;
        double seLon = (x + 1) / (double)n * 360.0 - 180.0;

        double nwLat = TileYToLat(y, n);
        double seLat = TileYToLat(y + 1, n);

        return (nwLat, nwLon, seLat, seLon);
    }

    /// <inheritdoc/>
    public void ClearMemoryCache()
    {
        _memCache.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double TileYToLat(int y, int n)
    {
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n)));
        return latRad * 180.0 / Math.PI;
    }

    private static string TileKey(int z, int x, int y) => $"{z}/{x}/{y}";

    private string DiskPath(int z, int x, int y)
    {
        var source = ConfigurationStore.Instance.Display.TileMapSource;
        string sourceName = source == TileSource.Custom ? "custom" : "osm";
        return Path.Combine(_cacheRoot, sourceName, z.ToString(), x.ToString(), $"{y}.png");
    }

    private string BuildUrl(int z, int x, int y)
    {
        string template = ConfigurationStore.Instance.Display.TileMapSource switch
        {
            TileSource.Custom => ConfigurationStore.Instance.Display.TileMapCustomUrl,
            _                 => OsmUrlTemplate
        };

        if (string.IsNullOrWhiteSpace(template))
            template = OsmUrlTemplate;

        return template
            .Replace("{z}", z.ToString())
            .Replace("{x}", x.ToString())
            .Replace("{y}", y.ToString());
    }

    private async Task DownloadTileAsync(int z, int x, int y, string key, string diskPath)
    {
        await _downloadSem.WaitAsync().ConfigureAwait(false);
        try
        {
            string url = BuildUrl(z, x, y);
            byte[] data = await _http.GetByteArrayAsync(url).ConfigureAwait(false);

            // Save to disk cache
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                await File.WriteAllBytesAsync(diskPath, data).ConfigureAwait(false);
            }
            catch { /* non-fatal – continue without disk cache */ }

            PutMemCache(key, data);
            FireCallbacks(key);
        }
        catch
        {
            // Network error – remove from pending so retry is possible next frame
        }
        finally
        {
            _pending.TryRemove(key, out _);
            _downloadSem.Release();
        }
    }

    private void PutMemCache(string key, byte[] data)
    {
        // Evict oldest entries when cache is full (simple: just clear half)
        if (_memCache.Count >= MemCacheMax)
        {
            int toRemove = MemCacheMax / 2;
            foreach (var k in _memCache.Keys)
            {
                if (toRemove-- <= 0) break;
                _memCache.TryRemove(k, out _);
            }
        }
        _memCache[key] = data;
    }

    private void RegisterCallback(string key, Action callback)
    {
        lock (_callbackLock)
        {
            if (!_callbacks.TryGetValue(key, out var list))
            {
                list = new List<Action>();
                _callbacks[key] = list;
            }
            list.Add(callback);
        }
    }

    private void FireCallbacks(string key)
    {
        List<Action>? list;
        lock (_callbackLock)
        {
            _callbacks.TryRemove(key, out list);
        }
        if (list == null) return;
        foreach (var cb in list)
        {
            try { cb(); } catch { /* ignore UI-side exceptions */ }
        }
    }
}
