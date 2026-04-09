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
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgValoniaGPS.Desktop.Services;

/// <summary>
/// Serial port GPS service for Desktop (Windows, macOS, Linux).
/// Reads NMEA sentences from a serial/COM port (USB u-blox, ArduSimple, etc.).
/// </summary>
public class SerialGpsService : ISerialGpsService, IDisposable
{
    private readonly ILogger<SerialGpsService> _logger;
    private SerialPort? _port;
    private readonly StringBuilder _lineBuffer = new();

    public bool IsConnected { get; private set; }
    public string? ConnectedPortName { get; private set; }

    public event EventHandler<string>? NmeaLineReceived;
    public event EventHandler<bool>? ConnectionStateChanged;

    public SerialGpsService(ILogger<SerialGpsService> logger)
    {
        _logger = logger;
    }

    public IEnumerable<string> GetAvailablePorts()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate serial ports");
            return Array.Empty<string>();
        }
    }

    public Task<bool> ConnectAsync(string portName, int baudRate)
    {
        DisconnectInternal();

        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 2000,
                WriteTimeout = 500,
                Encoding = Encoding.ASCII,
                NewLine = "\n"
            };
            _port.DataReceived += OnDataReceived;
            _port.ErrorReceived += OnErrorReceived;
            _port.Open();

            IsConnected = true;
            ConnectedPortName = portName;
            ConnectionStateChanged?.Invoke(this, true);
            _logger.LogInformation("Serial GPS connected on {Port} at {Baud} baud", portName, baudRate);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open serial port {Port}", portName);
            _port?.Dispose();
            _port = null;
            return Task.FromResult(false);
        }
    }

    public Task DisconnectAsync()
    {
        DisconnectInternal();
        return Task.CompletedTask;
    }

    private void DisconnectInternal()
    {
        if (_port == null) return;

        _port.DataReceived -= OnDataReceived;
        _port.ErrorReceived -= OnErrorReceived;

        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
        _port = null;

        if (IsConnected)
        {
            IsConnected = false;
            ConnectedPortName = null;
            ConnectionStateChanged?.Invoke(this, false);
            _logger.LogInformation("Serial GPS disconnected");
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port == null || !_port.IsOpen) return;

        try
        {
            var text = _port.ReadExisting();
            _lineBuffer.Append(text);

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading from serial port");
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        _logger.LogWarning("Serial port error: {Error}", e.EventType);
        DisconnectInternal();
    }

    public void Dispose()
    {
        DisconnectInternal();
    }
}
