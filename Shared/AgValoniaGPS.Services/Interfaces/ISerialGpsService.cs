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
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Serial port GPS service interface.
/// Connects to GPS receivers exposed as serial/COM ports (USB, RS-232).
/// Compatible with u-blox, ArduSimple, and other NMEA-capable devices.
/// </summary>
public interface ISerialGpsService
{
    /// <summary>Whether a serial GPS device is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Name of the currently open serial port, or null.</summary>
    string? ConnectedPortName { get; }

    /// <summary>Fired when a complete NMEA sentence is received.</summary>
    event EventHandler<string>? NmeaLineReceived;

    /// <summary>Fired when the connection state changes (true = connected, false = disconnected).</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Returns all serial port names available on this system.</summary>
    IEnumerable<string> GetAvailablePorts();

    /// <summary>
    /// Open the specified serial port at the given baud rate and start reading NMEA.
    /// Returns true on success.
    /// </summary>
    Task<bool> ConnectAsync(string portName, int baudRate);

    /// <summary>Close the serial port and stop reading.</summary>
    Task DisconnectAsync();

    /// <summary>Write raw bytes to the serial port (e.g. RTCM corrections).</summary>
    Task WriteAsync(byte[] data);
}
