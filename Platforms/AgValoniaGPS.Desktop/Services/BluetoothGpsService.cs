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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Bluetooth;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgValoniaGPS.Desktop.Services;

/// <summary>
/// Bluetooth LE GPS service for Windows Desktop.
/// Connects to GPS receivers exposing NMEA over Nordic UART Service (NUS).
/// Compatible with ArduSimple SimpleRTK2B and similar devices.
///
/// Nordic UART Service UUIDs:
///   Service:        6E400001-B5A3-F393-E0A9-E50E24DCCA9E
///   TX (notify):    6E400003-B5A3-F393-E0A9-E50E24DCCA9E  (device → app)
///   RX (write):     6E400002-B5A3-F393-E0A9-E50E24DCCA9E  (app → device, not used)
/// </summary>
public class BluetoothGpsService : IGpsBluetoothService, IDisposable
{
    private static readonly BluetoothUuid NusServiceUuid =
        BluetoothUuid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly BluetoothUuid NusTxCharUuid =
        BluetoothUuid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private readonly ILogger<BluetoothGpsService> _logger;

    private BluetoothDevice? _connectedDevice;
    private GattCharacteristic? _txCharacteristic;
    private readonly StringBuilder _lineBuffer = new();

    // Discovered devices from last scan: name → device
    private readonly Dictionary<string, BluetoothDevice> _scannedDevices = new();

    public bool IsConnected { get; private set; }
    public string? ConnectedDeviceName { get; private set; }
    public bool IsScanning { get; private set; }

    public event EventHandler<string>? NmeaLineReceived;
    public event EventHandler<bool>? ConnectionStateChanged;

    public BluetoothGpsService(ILogger<BluetoothGpsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scan for nearby BLE devices advertising the Nordic UART Service.
    /// Scan duration is ~8 seconds. Returns list of device names found.
    /// </summary>
    public async Task<IList<string>> ScanForDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning) return Array.Empty<string>();

        IsScanning = true;
        _scannedDevices.Clear();

        try
        {
            // Register advertisement handler
            void OnAdvertisement(object? sender, BluetoothAdvertisingEvent e)
            {
                if (string.IsNullOrEmpty(e.Name)) return;
                lock (_scannedDevices)
                {
                    _scannedDevices[e.Name] = e.Device;
                }
                _logger.LogDebug("BLE discovered: {Name}", e.Name);
            }

            Bluetooth.AdvertisementReceived += OnAdvertisement;

            try
            {
                // Start scanning – AcceptAllAdvertisements so we see devices
                // even if they don't advertise NUS in the advertisement PDU
                await Bluetooth.RequestLeScanAsync(new RequestLeScanOptions
                {
                    AcceptAllAdvertisements = true
                });

                // Wait for scan duration or cancellation
                await Task.Delay(8000, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Bluetooth.AdvertisementReceived -= OnAdvertisement;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE scan error");
        }
        finally
        {
            IsScanning = false;
        }

        lock (_scannedDevices)
        {
            return new List<string>(_scannedDevices.Keys);
        }
    }

    /// <summary>
    /// Connect to a BLE device by name (must appear in last ScanForDevicesAsync result).
    /// Connects to Nordic UART Service TX characteristic and subscribes to notifications.
    /// </summary>
    public async Task<bool> ConnectAsync(string deviceName)
    {
        await DisconnectAsync().ConfigureAwait(false);

        BluetoothDevice? device;
        lock (_scannedDevices)
        {
            _scannedDevices.TryGetValue(deviceName, out device);
        }

        if (device == null)
        {
            _logger.LogWarning("BLE device '{Name}' not found in scan results – run scan first", deviceName);
            return false;
        }

        try
        {
            _logger.LogInformation("BLE connecting to {Name}…", deviceName);
            await device.Gatt.ConnectAsync().ConfigureAwait(false);

            var service = await device.Gatt.GetPrimaryServiceAsync(NusServiceUuid).ConfigureAwait(false);
            if (service == null)
            {
                _logger.LogWarning("BLE: Nordic UART Service not found on {Name}", deviceName);
                device.Gatt.Disconnect();
                return false;
            }

            var txChar = await service.GetCharacteristicAsync(NusTxCharUuid).ConfigureAwait(false);
            if (txChar == null)
            {
                _logger.LogWarning("BLE: NUS TX characteristic not found on {Name}", deviceName);
                device.Gatt.Disconnect();
                return false;
            }

            txChar.CharacteristicValueChanged += OnCharacteristicValueChanged;
            await txChar.StartNotificationsAsync().ConfigureAwait(false);

            _connectedDevice = device;
            _txCharacteristic = txChar;
            IsConnected = true;
            ConnectedDeviceName = deviceName;

            device.GattServerDisconnected += OnDeviceDisconnected;

            ConnectionStateChanged?.Invoke(this, true);
            _logger.LogInformation("BLE connected to {Name}", deviceName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BLE connection failed for {Name}", deviceName);
            return false;
        }
    }

    /// <summary>Disconnect from the current BLE device.</summary>
    public async Task DisconnectAsync()
    {
        if (_txCharacteristic != null)
        {
            _txCharacteristic.CharacteristicValueChanged -= OnCharacteristicValueChanged;
            try { await _txCharacteristic.StopNotificationsAsync().ConfigureAwait(false); } catch { }
            _txCharacteristic = null;
        }

        if (_connectedDevice != null)
        {
            _connectedDevice.GattServerDisconnected -= OnDeviceDisconnected;
            _connectedDevice.Gatt.Disconnect();
            _connectedDevice = null;
        }

        if (IsConnected)
        {
            IsConnected = false;
            ConnectedDeviceName = null;
            ConnectionStateChanged?.Invoke(this, false);
            _logger.LogInformation("BLE disconnected");
        }
    }

    private void OnDeviceDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("BLE device disconnected unexpectedly");
        _txCharacteristic = null;
        _connectedDevice = null;
        IsConnected = false;
        ConnectedDeviceName = null;
        ConnectionStateChanged?.Invoke(this, false);
    }

    private void OnCharacteristicValueChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        var text = Encoding.ASCII.GetString(e.Value.ToArray());
        _lineBuffer.Append(text);

        // Extract complete NMEA lines (delimited by \n or \r\n)
        var buf = _lineBuffer.ToString();
        int newlineIdx;
        while ((newlineIdx = buf.IndexOf('\n')) >= 0)
        {
            var line = buf.Substring(0, newlineIdx).TrimEnd('\r');
            buf = buf.Substring(newlineIdx + 1);
            if (!string.IsNullOrWhiteSpace(line))
                NmeaLineReceived?.Invoke(this, line);
        }
        _lineBuffer.Clear();
        _lineBuffer.Append(buf);
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
