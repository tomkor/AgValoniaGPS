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

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Provides XYZ tile map tiles (OSM-compatible).
/// Handles asynchronous HTTP download and two-level caching (memory + disk).
/// </summary>
public interface ITileMapService
{
    /// <summary>
    /// Returns raw image bytes for the requested tile, or null if not yet cached.
    /// When null is returned the download is started automatically; <paramref name="onLoaded"/>
    /// is called when the tile becomes available so the caller can trigger a re-render.
    /// The returned array is owned by the cache — do not mutate it.
    /// </summary>
    byte[]? GetTile(int z, int x, int y, Action onLoaded);

    /// <summary>Convert WGS84 lat/lon to OSM tile coordinates at the given zoom level.</summary>
    (int tileX, int tileY) LatLonToTile(double lat, double lon, int zoom);

    /// <summary>
    /// Returns the NW and SE WGS84 corners of a tile.
    /// Result: (nwLat, nwLon, seLat, seLon)
    /// </summary>
    (double nwLat, double nwLon, double seLat, double seLon) TileBounds(int x, int y, int z);

    /// <summary>Drop all entries from the in-memory bitmap cache.</summary>
    void ClearMemoryCache();
}
