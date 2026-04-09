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
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.TileMap;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.TileMap;

/// <summary>
/// XYZ / WMS tile service with two-level cache (memory bytes + disk).
/// The caller (Views layer) is responsible for decoding and caching Bitmap objects.
/// </summary>
public class TileMapService : ITileMapService
{
    // ── Static singleton ─────────────────────────────────────────────────────
    private static TileMapService? _instance;
    public static TileMapService? Instance => _instance;

    // ── URL templates ─────────────────────────────────────────────────────────
    private const string OsmUrlTemplate   = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    private const string EsriUrlTemplate  = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
    private const string GeoportalWmsBase = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution";

    private const int ByteCacheMax     = 256;   // max tiles kept in memory (raw bytes)
    private const int MaxConcurrent    = 4;     // parallel downloads
    private const int FailCooldownSec  = 30;    // seconds before retrying a failed tile

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "AgValoniaGPS/1.0 (tile-client)" } },
        Timeout = TimeSpan.FromSeconds(15)
    };

    // ── Byte cache (key → raw image bytes) ────────────────────────────────────
    private readonly ConcurrentDictionary<string, byte[]> _byteCache = new();
    private readonly Queue<string> _evictionQueue = new();
    private readonly object _evictionLock = new();

    // ── Download coordination ─────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, bool> _pending = new();
    private readonly ConcurrentDictionary<string, List<Action>> _callbacks = new();
    private readonly object _callbackLock = new();

    // ── Negative cache (failed tiles) ─────────────────────────────────────────
    private readonly ConcurrentDictionary<string, DateTime> _failedTiles = new();

    private readonly SemaphoreSlim _downloadSem = new(MaxConcurrent, MaxConcurrent);

    // ── Disk cache root ───────────────────────────────────────────────────────
    private readonly string _cacheRoot;

    public TileMapService()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgValoniaGPS", "tile_cache");
        _instance = this;
    }

    // ── ITileMapService ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public byte[]? GetTile(int z, int x, int y, Action onLoaded)
    {
        string key = TileKey(z, x, y);

        // 1. Memory hit
        if (_byteCache.TryGetValue(key, out var cached))
            return cached;

        // 2. Skip recently failed tiles
        if (_failedTiles.TryGetValue(key, out var retryAfter) && DateTime.UtcNow < retryAfter)
            return null;

        // 3. Not in memory — start async load (disk or network); never block UI thread
        string diskPath = DiskPath(z, x, y);
        if (_pending.TryAdd(key, true))
        {
            RegisterCallback(key, onLoaded);
            _ = LoadTileAsync(z, x, y, key, diskPath);
        }
        else
        {
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
        double nwLat = TileYToLat(y,     n);
        double seLat = TileYToLat(y + 1, n);
        return (nwLat, nwLon, seLat, seLon);
    }

    /// <inheritdoc/>
    public void ClearMemoryCache()
    {
        lock (_evictionLock)
        {
            _byteCache.Clear();
            _evictionQueue.Clear();
        }
        _failedTiles.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double TileYToLat(int y, int n)
    {
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n)));
        return latRad * 180.0 / Math.PI;
    }

    /// <summary>Cache key includes source prefix so switching sources never serves stale tiles.</summary>
    private static string TileKey(int z, int x, int y)
    {
        string src = ConfigurationStore.Instance.Display.TileMapSource switch
        {
            TileSource.EsriWorldImagery => "e",
            TileSource.GeoportalOrto    => "g",
            TileSource.Custom           => "c",
            _                           => "o"
        };
        return $"{src}/{z}/{x}/{y}";
    }

    private string DiskPath(int z, int x, int y)
    {
        string sourceName = ConfigurationStore.Instance.Display.TileMapSource switch
        {
            TileSource.Custom           => "custom",
            TileSource.EsriWorldImagery => "esri",
            TileSource.GeoportalOrto    => "geoportal",
            _                           => "osm"
        };
        return Path.Combine(_cacheRoot, sourceName, z.ToString(), x.ToString(), $"{y}.jpg");
    }

    private string BuildUrl(int z, int x, int y)
    {
        var source = ConfigurationStore.Instance.Display.TileMapSource;

        if (source == TileSource.GeoportalOrto)
            return BuildWmsUrl(z, x, y);

        string template = source switch
        {
            TileSource.Custom           => ConfigurationStore.Instance.Display.TileMapCustomUrl,
            TileSource.EsriWorldImagery => EsriUrlTemplate,
            _                           => OsmUrlTemplate
        };

        if (string.IsNullOrWhiteSpace(template))
            template = OsmUrlTemplate;

        return template
            .Replace("{z}", z.ToString())
            .Replace("{x}", x.ToString())
            .Replace("{y}", y.ToString());
    }

    private static string BuildWmsUrl(int z, int x, int y)
    {
        int n = 1 << z;
        double lonMin = (double)x       / n * 360.0 - 180.0;
        double lonMax = (double)(x + 1) / n * 360.0 - 180.0;
        double latMax = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y       / n))) * 180.0 / Math.PI;
        double latMin = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * (y + 1) / n))) * 180.0 / Math.PI;

        string bbox = string.Format(CultureInfo.InvariantCulture,
            "{0:F6},{1:F6},{2:F6},{3:F6}", lonMin, latMin, lonMax, latMax);

        return GeoportalWmsBase +
               "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
               "&LAYERS=Raster&STYLES=&CRS=CRS:84" +
               $"&BBOX={bbox}&WIDTH=256&HEIGHT=256&FORMAT=image/jpeg";
    }

    private async Task LoadTileAsync(int z, int x, int y, string key, string diskPath)
    {
        // Check disk cache first — off UI thread
        if (File.Exists(diskPath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                PutByteCache(key, bytes);
                _pending.TryRemove(key, out _);
                FireCallbacks(key);
                return;
            }
            catch
            {
                try { File.Delete(diskPath); } catch { }
            }
        }

        // Download from network
        await DownloadTileAsync(z, x, y, key, diskPath).ConfigureAwait(false);
    }

    private async Task DownloadTileAsync(int z, int x, int y, string key, string diskPath)
    {
        await _downloadSem.WaitAsync().ConfigureAwait(false);
        try
        {
            string url = BuildUrl(z, x, y);

            // Use SendAsync to inspect Content-Type before caching.
            // WMS servers return XML ServiceException (1 KB) on error — must not cache those.
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected content type: {contentType}");

            byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            // Save to disk cache
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                await File.WriteAllBytesAsync(diskPath, data).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }

            PutByteCache(key, data);
            FireCallbacks(key);
        }
        catch
        {
            // Don't retry for FailCooldownSec seconds
            _failedTiles[key] = DateTime.UtcNow.AddSeconds(FailCooldownSec);
            // Clear callbacks so they don't accumulate across retries
            ClearCallbacks(key);
        }
        finally
        {
            _pending.TryRemove(key, out _);
            _downloadSem.Release();
        }
    }

    private void PutByteCache(string key, byte[] data)
    {
        lock (_evictionLock)
        {
            while (_evictionQueue.Count >= ByteCacheMax)
            {
                var oldest = _evictionQueue.Dequeue();
                _byteCache.TryRemove(oldest, out _);
            }
            if (_byteCache.TryAdd(key, data))
                _evictionQueue.Enqueue(key);
        }
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
            if (list.Count < 8) // cap to prevent unbounded growth
                list.Add(callback);
        }
    }

    private void FireCallbacks(string key)
    {
        List<Action>? list;
        lock (_callbackLock)
            _callbacks.TryRemove(key, out list);

        if (list == null) return;
        foreach (var cb in list)
            try { cb(); } catch { }
    }

    private void ClearCallbacks(string key)
    {
        lock (_callbackLock)
            _callbacks.TryRemove(key, out _);
    }
}
