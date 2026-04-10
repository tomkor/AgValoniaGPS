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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Bluetooth;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging;

// InTheHand.BluetoothLE uses Tmds.DBus (Linux/BlueZ) as its non-Windows backend.
// On macOS it tries D-Bus instead of CoreBluetooth and immediately throws a
// ConnectException. The BLE service therefore short-circuits on macOS and returns
// an informative message so the user can fall back to the USB serial connection.

namespace AgValoniaGPS.Desktop.Services;

/// <summary>
/// Bluetooth LE GPS service for Windows Desktop.
/// Connects to GPS receivers exposing NMEA over Nordic UART Service (NUS).
/// Compatible with ArduSimple SimpleRTK2B and similar devices.
///
/// Nordic UART Service UUIDs:
///   Service:        6E400001-B5A3-F393-E0A9-E50E24DCCA9E
///   TX (notify):    6E400003-B5A3-F393-E0A9-E50E24DCCA9E  (device → app)
///   RX (write):     6E400002-B5A3-F393-E0A9-E50E24DCCA9E  (app → device, RTCM corrections)
/// </summary>
public class BluetoothGpsService : IGpsBluetoothService, IDisposable
{
    private static readonly BluetoothUuid NusServiceUuid =
        BluetoothUuid.FromGuid(new Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"));
    private static readonly BluetoothUuid NusTxCharUuid =
        BluetoothUuid.FromGuid(new Guid("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"));
    private static readonly BluetoothUuid NusRxCharUuid =
        BluetoothUuid.FromGuid(new Guid("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"));

    private readonly ILogger<BluetoothGpsService> _logger;

    private BluetoothDevice? _connectedDevice;
    private GattCharacteristic? _txCharacteristic;
    private GattCharacteristic? _rxCharacteristic;
    private readonly StringBuilder _lineBuffer = new();
    private readonly SemaphoreSlim _rtcmWriteLock = new(1, 1);
    private ulong _rtcmBytesSent;
    private DateTime _lastRtcmProgressLogUtc = DateTime.MinValue;

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
    /// Also includes already-paired devices. Scan duration is ~8 seconds.
    /// Returns list of device names found.
    /// </summary>
    public async Task<IList<string>> ScanForDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning) return Array.Empty<string>();

        // InTheHand.BluetoothLE on macOS routes through Tmds.DBus (Linux BlueZ)
        // instead of CoreBluetooth and immediately throws ConnectException.
        // Return a clear error so the user knows to use USB serial instead.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _logger.LogWarning("BLE scanning is not supported on macOS with this build. Use USB serial connection instead.");
            return Array.Empty<string>();
        }

        IsScanning = true;
        _scannedDevices.Clear();

        _logger.LogInformation("BLE scan starting on {OS}", RuntimeInformation.OSDescription);

        try
        {
            // Check Bluetooth availability
            var available = await Bluetooth.GetAvailabilityAsync().ConfigureAwait(false);
            _logger.LogInformation("BLE availability: {Available}", available);
            if (!available)
            {
                _logger.LogWarning("BLE not available on this system – check Bluetooth is enabled and app has permission");
                return Array.Empty<string>();
            }

            // Include already-paired devices first
            try
            {
                var paired = await Bluetooth.GetPairedDevicesAsync().ConfigureAwait(false);
                _logger.LogInformation("BLE paired devices count: {Count}", paired.Count);
                foreach (var d in paired)
                {
                    if (string.IsNullOrEmpty(d.Name)) continue;
                    lock (_scannedDevices)
                    {
                        _scannedDevices[d.Name] = d;
                    }
                    _logger.LogInformation("BLE paired device: {Name}", d.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BLE GetPairedDevicesAsync failed");
            }

            // Register advertisement handler
            void OnAdvertisement(object? sender, BluetoothAdvertisingEvent e)
            {
                if (string.IsNullOrEmpty(e.Name)) return;
                lock (_scannedDevices)
                {
                    _scannedDevices[e.Name] = e.Device;
                }
                _logger.LogInformation("BLE advertisement received: {Name}", e.Name);
            }

            Bluetooth.AdvertisementReceived += OnAdvertisement;

            BluetoothLEScan? scan = null;
            try
            {
                _logger.LogInformation("BLE calling RequestLEScanAsync…");
                // RequestLEScanAsync returns a scan object that MUST be kept alive –
                // the scan runs only as long as this object is referenced.
                // On some platforms (macOS) this may throw – fall back to paired-only mode.
                try
                {
                    scan = await Bluetooth.RequestLEScanAsync(new BluetoothLEScanOptions
                    {
                        AcceptAllAdvertisements = true
                    }).ConfigureAwait(false);
                    _logger.LogInformation("BLE scan started, waiting 10 s…");
                    await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception scanEx)
                {
                    _logger.LogWarning(scanEx, "BLE RequestLEScanAsync failed ({Type}), using paired-devices only", scanEx.GetType().Name);
                    // Still wait briefly so paired devices list is returned
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                scan?.Stop();
                Bluetooth.AdvertisementReceived -= OnAdvertisement;
                _logger.LogInformation("BLE scan stopped, found {Count} device(s)", _scannedDevices.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BLE scan error – {Type}: {Message}", ex.GetType().Name, ex.Message);
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

            var rxChar = await service.GetCharacteristicAsync(NusRxCharUuid).ConfigureAwait(false);
            if (rxChar == null)
                _logger.LogWarning("BLE: NUS RX characteristic not found on {Name} (RTCM over BLE unavailable)", deviceName);

            txChar.CharacteristicValueChanged += OnCharacteristicValueChanged;
            await txChar.StartNotificationsAsync().ConfigureAwait(false);

            _connectedDevice = device;
            _txCharacteristic = txChar;
            _rxCharacteristic = rxChar;
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
        _rxCharacteristic = null;

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
        _rxCharacteristic = null;
        _connectedDevice = null;
        IsConnected = false;
        ConnectedDeviceName = null;
        ConnectionStateChanged?.Invoke(this, false);
    }

    public async Task<bool> WriteRtcmAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _rxCharacteristic == null || data.Length == 0)
            return false;

        var lockTaken = false;
        try
        {
            await _rtcmWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;

            var canWriteWithoutResponse =
                (_rxCharacteristic.Properties & GattCharacteristicProperties.WriteWithoutResponse) != 0;
            var canWriteWithResponse =
                (_rxCharacteristic.Properties & GattCharacteristicProperties.Write) != 0;

            if (!canWriteWithoutResponse && !canWriteWithResponse)
                return false;

            // Keep chunks conservative across BLE stacks/modules.
            var maxChunk = canWriteWithoutResponse ? 180 : 20;
            var offset = 0;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var len = Math.Min(maxChunk, data.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, offset, chunk, 0, len);

                if (canWriteWithoutResponse)
                    await _rxCharacteristic.WriteValueWithoutResponseAsync(chunk).ConfigureAwait(false);
                else
                    await _rxCharacteristic.WriteValueWithResponseAsync(chunk).ConfigureAwait(false);
                offset += len;
            }

            _rtcmBytesSent += (ulong)data.Length;
            var now = DateTime.UtcNow;
            if ((now - _lastRtcmProgressLogUtc).TotalSeconds >= 3)
            {
                _lastRtcmProgressLogUtc = now;
                _logger.LogInformation("BLE RTCM forwarded: {Kb} KB", _rtcmBytesSent / 1024);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE RTCM write failed");
            return false;
        }
        finally
        {
            if (lockTaken)
                _rtcmWriteLock.Release();
        }
    }

    private void OnCharacteristicValueChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        var text = Encoding.ASCII.GetString(e.Value ?? Array.Empty<byte>());
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
        _rtcmWriteLock.Dispose();
    }
}
