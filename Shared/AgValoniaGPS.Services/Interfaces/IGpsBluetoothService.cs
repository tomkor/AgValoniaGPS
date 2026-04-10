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
using System.Threading;
using System.Threading.Tasks;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Bluetooth LE GPS service interface.
/// Connects to GPS receivers that expose NMEA data over Nordic UART Service (NUS).
/// Used for devices like ArduSimple SimpleRTK2B with BLE module.
/// </summary>
public interface IGpsBluetoothService
{
    /// <summary>Whether a BLE GPS device is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Name of the currently connected BLE device, or null.</summary>
    string? ConnectedDeviceName { get; }

    /// <summary>Whether a scan is currently in progress.</summary>
    bool IsScanning { get; }

    /// <summary>Fired when a complete NMEA sentence is received over BLE.</summary>
    event EventHandler<string>? NmeaLineReceived;

    /// <summary>Fired when the connection state changes (true = connected, false = disconnected).</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Scan for nearby BLE devices that expose the Nordic UART Service.
    /// Returns device names. Scan runs for approximately 8 seconds.
    /// </summary>
    Task<IList<string>> ScanForDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Connect to a previously discovered BLE device by name.
    /// </summary>
    Task<bool> ConnectAsync(string deviceName);

    /// <summary>Disconnect from the current BLE device.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Write RTCM correction bytes to the BLE GPS receiver (typically NUS RX characteristic).
    /// Returns true if the data was accepted for sending.
    /// </summary>
    Task<bool> WriteRtcmAsync(byte[] data, CancellationToken cancellationToken = default);
}
