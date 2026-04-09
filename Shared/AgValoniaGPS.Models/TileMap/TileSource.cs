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

namespace AgValoniaGPS.Models.TileMap;

/// <summary>
/// Available tile map sources for the background map layer.
/// </summary>
public enum TileSource
{
    /// <summary>OpenStreetMap standard tile layer (XYZ, free, no API key required)</summary>
    OpenStreetMap,

    /// <summary>Custom XYZ tile URL template supplied by the user ({z}/{x}/{y} placeholders)</summary>
    Custom
}
