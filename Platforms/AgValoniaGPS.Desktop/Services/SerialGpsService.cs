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
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgValoniaGPS.Desktop.Services;

/// <summary>
/// Serial port GPS service for Desktop (Windows, macOS, Linux).
/// Reads NMEA sentences from a serial/COM port (USB u-blox, ArduSimple, etc.).
///
/// Uses a manual ReadLineAsync loop instead of SerialPort.DataReceived to avoid
/// a known .NET race condition bug (NullReferenceException in
/// SerialStream.EventLoopRunner.CallReceiveEvents at high baud rates).
/// </summary>
public class SerialGpsService : ISerialGpsService, IDisposable
{
    private readonly ILogger<SerialGpsService> _logger;
    private SerialPort? _port;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

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
        StopReadLoop();
        ClosePort();

        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout  = SerialPort.InfiniteTimeout,
                WriteTimeout = 500,
                Encoding     = Encoding.ASCII,
                NewLine      = "\n"
            };
            _port.Open();

            IsConnected     = true;
            ConnectedPortName = portName;
            ConnectionStateChanged?.Invoke(this, true);
            _logger.LogInformation("Serial GPS connected on {Port} at {Baud} baud", portName, baudRate);

            _readCts  = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoop(_readCts.Token));

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

    public async Task DisconnectAsync()
    {
        await StopReadLoopAsync();
        ClosePort();
    }

    public Task WriteAsync(byte[] data)
    {
        var port = _port;
        if (port == null || !port.IsOpen || data.Length == 0)
            return Task.CompletedTask;
        try
        {
            port.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write to serial port");
        }
        return Task.CompletedTask;
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task ReadLoop(CancellationToken ct)
    {
        var port = _port;
        if (port == null) return;

        // StreamReader wraps the BaseStream; leaveOpen=true so we control port lifetime.
        using var reader = new StreamReader(port.BaseStream, Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

        var sb = new StringBuilder();
        var buf = new char[256];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buf, ct);
                if (read == 0) break; // EOF / port closed

                for (int i = 0; i < read; i++)
                {
                    char c = buf[i];
                    if (c == '\n')
                    {
                        // Strip trailing CR if present
                        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                            sb.Length--;

                        var line = sb.ToString();
                        sb.Clear();

                        if (!string.IsNullOrWhiteSpace(line))
                            NmeaLineReceived?.Invoke(this, line);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Serial read error on {Port}", ConnectedPortName);
            // Port unexpectedly disconnected — notify on background thread is fine
            ClosePort();
        }
    }

    private void StopReadLoop()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
    }

    private async Task StopReadLoopAsync()
    {
        if (_readCts == null) return;
        _readCts.Cancel();
        if (_readTask != null)
        {
            try { await _readTask.ConfigureAwait(false); }
            catch { /* expected cancellation/IO exceptions */ }
        }
        _readCts.Dispose();
        _readCts  = null;
        _readTask = null;
    }

    private void ClosePort()
    {
        if (_port == null) return;

        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
        _port = null;

        if (IsConnected)
        {
            IsConnected       = false;
            ConnectedPortName = null;
            ConnectionStateChanged?.Invoke(this, false);
            _logger.LogInformation("Serial GPS disconnected");
        }
    }

    public void Dispose()
    {
        StopReadLoop();
        ClosePort();
    }
}
