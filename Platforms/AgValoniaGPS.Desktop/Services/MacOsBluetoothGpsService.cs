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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgValoniaGPS.Desktop.Services;

/// <summary>
/// CoreBluetooth BLE GPS service for macOS.
///
/// InTheHand.BluetoothLE incorrectly uses Linux D-Bus (BlueZ) on macOS instead
/// of CoreBluetooth. This implementation calls CoreBluetooth directly via the
/// Objective-C runtime so no extra packages or platform-specific TFM are needed.
///
/// Supports Nordic UART Service (NUS) — compatible with ArduSimple SimpleRTK2B,
/// u-blox BLE modules, and similar GNSS receivers.
/// </summary>
public sealed class MacOsBluetoothGpsService : IGpsBluetoothService, IDisposable
{
    // ── Objective-C runtime P/Invoke ─────────────────────────────────────────

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send1(IntPtr obj, IntPtr sel, IntPtr a1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send2(IntPtr obj, IntPtr sel, IntPtr a1, IntPtr a2);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send3Long(IntPtr obj, IntPtr sel, IntPtr a1, IntPtr a2, nint a3);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long SendLong1(IntPtr obj, IntPtr sel, nint a1);

    // setNotifyValue:forCharacteristic: — BOOL is byte on ARM64/x86-64 ABI
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendNotify(IntPtr obj, IntPtr sel, byte a1, IntPtr a2);

    // Returns NSInteger (long on 64-bit)
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long SendLong(IntPtr obj, IntPtr sel);

    // Returns NSUInteger (nuint)
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendUInt(IntPtr obj, IntPtr sel);

    // objectAtIndex: — takes NSUInteger
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIdx(IntPtr obj, IntPtr sel, nuint idx);

    // arrayWithObjects:count: — takes (id *, NSUInteger)
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendArr(IntPtr obj, IntPtr sel, IntPtr objs, nuint count);

    // initWithBytes:length: — takes (const void *, NSUInteger)
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtrUInt(IntPtr obj, IntPtr sel, IntPtr a1, nuint a2);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extra);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_retain(IntPtr obj);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern void objc_release(IntPtr obj);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dispatch_queue_create(string label, IntPtr attr);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern void dispatch_release(IntPtr queue);

    // ── Cached selectors ─────────────────────────────────────────────────────

    private static readonly IntPtr SelAlloc                 = sel_registerName("alloc");
    private static readonly IntPtr SelInit                  = sel_registerName("init");
    private static readonly IntPtr SelInitWithDelegateQueue = sel_registerName("initWithDelegate:queue:");
    private static readonly IntPtr SelScanForPeripherals    = sel_registerName("scanForPeripheralsWithServices:options:");
    private static readonly IntPtr SelStopScan              = sel_registerName("stopScan");
    private static readonly IntPtr SelConnectPeripheral     = sel_registerName("connectPeripheral:options:");
    private static readonly IntPtr SelCancelPeripheral      = sel_registerName("cancelPeripheralConnection:");
    private static readonly IntPtr SelDiscoverServices      = sel_registerName("discoverServices:");
    private static readonly IntPtr SelDiscoverChars         = sel_registerName("discoverCharacteristics:forService:");
    private static readonly IntPtr SelSetNotifyValue        = sel_registerName("setNotifyValue:forCharacteristic:");
    private static readonly IntPtr SelWriteValueForCharType = sel_registerName("writeValue:forCharacteristic:type:");
    private static readonly IntPtr SelMaximumWriteLenForType = sel_registerName("maximumWriteValueLengthForType:");
    private static readonly IntPtr SelSetDelegate           = sel_registerName("setDelegate:");
    private static readonly IntPtr SelState                 = sel_registerName("state");
    private static readonly IntPtr SelName                  = sel_registerName("name");
    private static readonly IntPtr SelIdentifier            = sel_registerName("identifier");
    private static readonly IntPtr SelUTF8String            = sel_registerName("UTF8String");
    private static readonly IntPtr SelValue                 = sel_registerName("value");
    private static readonly IntPtr SelBytes                 = sel_registerName("bytes");
    private static readonly IntPtr SelLength                = sel_registerName("length");
    private static readonly IntPtr SelProperties            = sel_registerName("properties");
    private static readonly IntPtr SelUUID                  = sel_registerName("UUID");
    private static readonly IntPtr SelUUIDString            = sel_registerName("UUIDString");
    private static readonly IntPtr SelCharacteristics       = sel_registerName("characteristics");
    private static readonly IntPtr SelServices              = sel_registerName("services");
    private static readonly IntPtr SelObjectAtIndex         = sel_registerName("objectAtIndex:");
    private static readonly IntPtr SelCount                 = sel_registerName("count");
    private static readonly IntPtr SelUUIDWithString        = sel_registerName("UUIDWithString:");
    private static readonly IntPtr SelInitWithUTF8          = sel_registerName("initWithUTF8String:");
    private static readonly IntPtr SelInitWithBytesLength   = sel_registerName("initWithBytes:length:");
    private static readonly IntPtr SelArrayWithObjsCount    = sel_registerName("arrayWithObjects:count:");

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string NusServiceUuidString = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";
    private const string NusTxCharUuidString  = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E";
    private const string NusRxCharUuidString  = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E";
    private const long   CBManagerStatePoweredOn = 5;
    private const long   CBCharacteristicPropertyWriteWithoutResponse = 0x04;
    private const long   CBCharacteristicPropertyWrite = 0x08;
    private const nint   CBCharacteristicWriteWithResponse = 0;
    private const nint   CBCharacteristicWriteWithoutResponse = 1;

    // ── Static delegate class (shared across all instances) ──────────────────

    // Keep delegate function pointers alive so GC doesn't collect them
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidUpdateStateD(IntPtr self, IntPtr sel, IntPtr central);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidDiscoverPeripheralD(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral, IntPtr adv, IntPtr rssi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidConnectPeripheralD(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidDisconnectPeripheralD(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidDiscoverServicesD(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidDiscoverCharsD(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr service, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidUpdateValueD(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr characteristic, IntPtr error);

    // Static references to prevent GC collection
    private static DidUpdateStateD?          _sDidUpdateState;
    private static DidDiscoverPeripheralD?   _sDidDiscoverPeripheral;
    private static DidConnectPeripheralD?    _sDidConnect;
    private static DidDisconnectPeripheralD? _sDidDisconnect;
    private static DidDiscoverServicesD?     _sDidDiscoverServices;
    private static DidDiscoverCharsD?        _sDidDiscoverChars;
    private static DidUpdateValueD?          _sDidUpdateValue;

    private static IntPtr _delegateClass;
    private static readonly object _delegateClassLock = new();

    // Map delegate object ptr → service instance for ObjC callback routing
    private static readonly ConcurrentDictionary<IntPtr, MacOsBluetoothGpsService> _instances = new();

    // ── Instance state ────────────────────────────────────────────────────────

    private readonly ILogger<MacOsBluetoothGpsService> _logger;

    private IntPtr _dispatchQueue;
    private IntPtr _centralManager;
    private IntPtr _delegateObj;

    private readonly ConcurrentDictionary<string, IntPtr> _discovered = new();
    private IntPtr _connectedPeripheral;
    private IntPtr _rxCharacteristic;
    private nint _rxWriteType = CBCharacteristicWriteWithoutResponse;

    private TaskCompletionSource<bool>? _powerOnTcs;
    private TaskCompletionSource<bool>? _connectTcs;
    private bool _bleReady;
    private readonly SemaphoreSlim _rtcmWriteLock = new(1, 1);
    private ulong _rtcmBytesSent;
    private DateTime _lastRtcmProgressLogUtc = DateTime.MinValue;

    private readonly StringBuilder _lineBuffer = new();

    public bool IsConnected      { get; private set; }
    public string? ConnectedDeviceName { get; private set; }
    public bool IsScanning       { get; private set; }

    public event EventHandler<string>? NmeaLineReceived;
    public event EventHandler<bool>?  ConnectionStateChanged;

    public MacOsBluetoothGpsService(ILogger<MacOsBluetoothGpsService> logger)
    {
        _logger = logger;
        EnsureDelegateClassCreated();
        InitializeCentral();
    }

    // ── Delegate class (created once per process) ─────────────────────────────

    private static void EnsureDelegateClassCreated()
    {
        lock (_delegateClassLock)
        {
            if (_delegateClass != IntPtr.Zero) return;

            var nsObj = objc_getClass("NSObject");
            _delegateClass = objc_allocateClassPair(nsObj, "AgVGpsCBDelegate", 0);

            _sDidUpdateState = static (self, sel, central) =>
            {
                SafeCallback(self, s => s.OnDidUpdateState(central), "centralManagerDidUpdateState");
            };
            class_addMethod(_delegateClass,
                sel_registerName("centralManagerDidUpdateState:"),
                Marshal.GetFunctionPointerForDelegate(_sDidUpdateState), "v@:@");

            _sDidDiscoverPeripheral = static (self, sel, central, peripheral, adv, rssi) =>
            {
                SafeCallback(self, s => s.OnDidDiscoverPeripheral(peripheral), "didDiscoverPeripheral");
            };
            class_addMethod(_delegateClass,
                sel_registerName("centralManager:didDiscoverPeripheral:advertisementData:RSSI:"),
                Marshal.GetFunctionPointerForDelegate(_sDidDiscoverPeripheral), "v@:@@@@");

            _sDidConnect = static (self, sel, central, peripheral) =>
            {
                SafeCallback(self, s => s.OnDidConnect(peripheral), "didConnectPeripheral");
            };
            class_addMethod(_delegateClass,
                sel_registerName("centralManager:didConnectPeripheral:"),
                Marshal.GetFunctionPointerForDelegate(_sDidConnect), "v@:@@");

            _sDidDisconnect = static (self, sel, central, peripheral, error) =>
            {
                SafeCallback(self, s => s.OnDidDisconnect(), "didDisconnectPeripheral");
            };
            class_addMethod(_delegateClass,
                sel_registerName("centralManager:didDisconnectPeripheral:error:"),
                Marshal.GetFunctionPointerForDelegate(_sDidDisconnect), "v@:@@@");

            _sDidDiscoverServices = static (self, sel, peripheral, error) =>
            {
                SafeCallback(self, s => s.OnDidDiscoverServices(peripheral), "didDiscoverServices");
            };
            class_addMethod(_delegateClass,
                sel_registerName("peripheral:didDiscoverServices:"),
                Marshal.GetFunctionPointerForDelegate(_sDidDiscoverServices), "v@:@@");

            _sDidDiscoverChars = static (self, sel, peripheral, service, error) =>
            {
                SafeCallback(self, s => s.OnDidDiscoverCharacteristics(peripheral, service), "didDiscoverCharacteristics");
            };
            class_addMethod(_delegateClass,
                sel_registerName("peripheral:didDiscoverCharacteristicsForService:error:"),
                Marshal.GetFunctionPointerForDelegate(_sDidDiscoverChars), "v@:@@@");

            _sDidUpdateValue = static (self, sel, peripheral, characteristic, error) =>
            {
                SafeCallback(self, s => s.OnDidUpdateValue(characteristic), "didUpdateValue");
            };
            class_addMethod(_delegateClass,
                sel_registerName("peripheral:didUpdateValueForCharacteristic:error:"),
                Marshal.GetFunctionPointerForDelegate(_sDidUpdateValue), "v@:@@@");

            objc_registerClassPair(_delegateClass);
        }
    }

    private static void SafeCallback(IntPtr self, Action<MacOsBluetoothGpsService> callback, string name)
    {
        if (!_instances.TryGetValue(self, out var svc)) return;
        try
        {
            callback(svc);
        }
        catch (Exception ex)
        {
            svc._logger.LogError(ex, "BLE callback crashed: {Callback}", name);
        }
    }

    // ── Initialization ────────────────────────────────────────────────────────

    private void InitializeCentral()
    {
        _dispatchQueue = dispatch_queue_create("com.agvaloniaagps.ble", IntPtr.Zero);

        // [AgVGpsCBDelegate alloc] init
        _delegateObj = Send(Send(_delegateClass, SelAlloc), SelInit);
        _instances[_delegateObj] = this;

        // Create CBCentralManager
        var cbCls = objc_getClass("CBCentralManager");
        _centralManager = Send2(Send(cbCls, SelAlloc), SelInitWithDelegateQueue,
            _delegateObj, _dispatchQueue);

        _logger.LogInformation("CBCentralManager initialized on macOS");
    }

    // ── CoreBluetooth delegate callbacks ─────────────────────────────────────

    private void OnDidUpdateState(IntPtr central)
    {
        var state = SendLong(central, SelState);
        _logger.LogInformation("CBCentralManager state changed: {State}", state);
        _bleReady = (state == CBManagerStatePoweredOn);
        _powerOnTcs?.TrySetResult(_bleReady);
    }

    private void OnDidDiscoverPeripheral(IntPtr peripheral)
    {
        string? name = null;

        var nameNs = Send(peripheral, SelName);
        if (nameNs != IntPtr.Zero)
        {
            var namePtr = Send(nameNs, SelUTF8String);
            if (namePtr != IntPtr.Zero)
                name = Marshal.PtrToStringUTF8(namePtr);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var nsUuid = Send(peripheral, SelIdentifier); // CBPeripheral.identifier -> NSUUID
            var uuid = GetUUIDString(nsUuid).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(uuid))
                return;
            name = $"Unknown ({uuid})";
        }

        RememberDiscoveredPeripheral(name, peripheral);
        _logger.LogInformation("BLE discovered: {Name}", name);
    }

    private void OnDidConnect(IntPtr peripheral)
    {
        _logger.LogInformation("BLE peripheral connected, discovering NUS service");
        Send1(peripheral, SelSetDelegate, _delegateObj);

        var nusUuid = MakeCBUUID(NusServiceUuidString);
        var arr = MakeNSArray(new[] { nusUuid });
        Send1(peripheral, SelDiscoverServices, arr);
    }

    private void OnDidDiscoverServices(IntPtr peripheral)
    {
        var servicesArr = Send(peripheral, SelServices);
        if (servicesArr == IntPtr.Zero) { _connectTcs?.TrySetResult(false); return; }

        var count = SendUInt(servicesArr, SelCount);
        var nusUuidStr = GetUUIDString(MakeCBUUID(NusServiceUuidString)).ToUpperInvariant();

        for (nuint i = 0; i < count; i++)
        {
            var svc = SendIdx(servicesArr, SelObjectAtIndex, i);
            var cbuuid = Send(svc, SelUUID); // CBService.UUID -> CBUUID
            var svcUuidStr = GetCBUUIDString(cbuuid).ToUpperInvariant();
            if (svcUuidStr == nusUuidStr)
            {
                // Discover all characteristics in NUS service (some bridges expose
                // writable uplink on non-standard UUIDs or merged TX/RX char).
                Send2(peripheral, SelDiscoverChars, IntPtr.Zero, svc);
                return;
            }
        }

        _logger.LogWarning("BLE: NUS service not found on peripheral");
        _connectTcs?.TrySetResult(false);
    }

    private void OnDidDiscoverCharacteristics(IntPtr peripheral, IntPtr service)
    {
        var charsArr = Send(service, SelCharacteristics);
        if (charsArr == IntPtr.Zero) { _connectTcs?.TrySetResult(false); return; }

        var count = SendUInt(charsArr, SelCount);
        var txUuidStr = GetCBUUIDString(MakeCBUUID(NusTxCharUuidString)).ToUpperInvariant();
        var rxUuidStr = GetCBUUIDString(MakeCBUUID(NusRxCharUuidString)).ToUpperInvariant();
        IntPtr txChar = IntPtr.Zero;
        IntPtr rxChar = IntPtr.Zero;
        IntPtr fallbackWriteChar = IntPtr.Zero;
        long selectedWriteProps = 0;
        string selectedWriteUuid = string.Empty;

        for (nuint i = 0; i < count; i++)
        {
            var ch = SendIdx(charsArr, SelObjectAtIndex, i);
            var cbuuid = Send(ch, SelUUID); // CBCharacteristic.UUID -> CBUUID
            var charUuidStr = GetCBUUIDString(cbuuid).ToUpperInvariant();
            var props = SendLong(ch, SelProperties);
            var canWrite = (props & (CBCharacteristicPropertyWrite | CBCharacteristicPropertyWriteWithoutResponse)) != 0;

            _logger.LogInformation("BLE characteristic discovered: {Uuid}, props=0x{Props:X}", charUuidStr, props);

            if (charUuidStr == txUuidStr) txChar = ch;
            else if (charUuidStr == rxUuidStr) rxChar = ch;

            if (canWrite && fallbackWriteChar == IntPtr.Zero)
            {
                fallbackWriteChar = ch;
                selectedWriteProps = props;
                selectedWriteUuid = charUuidStr;
            }
        }

        if (txChar == IntPtr.Zero)
        {
            _logger.LogWarning("BLE: NUS TX characteristic not found");
            _connectTcs?.TrySetResult(false);
            return;
        }

        var selectedWriteChar = rxChar != IntPtr.Zero ? rxChar : fallbackWriteChar;
        if (rxChar != IntPtr.Zero)
        {
            selectedWriteProps = SendLong(rxChar, SelProperties);
            selectedWriteUuid = rxUuidStr;
        }

        ReplaceRxCharacteristic(selectedWriteChar);
        if (selectedWriteChar == IntPtr.Zero)
        {
            _logger.LogWarning("BLE: NUS RX characteristic not found (RTCM over BLE unavailable)");
            _rxWriteType = CBCharacteristicWriteWithoutResponse;
        }
        else
        {
            _rxWriteType = (selectedWriteProps & CBCharacteristicPropertyWrite) != 0
                ? CBCharacteristicWriteWithResponse
                : CBCharacteristicWriteWithoutResponse;
            _logger.LogInformation("BLE write characteristic selected: {Uuid} ({Mode}, props=0x{Props:X})",
                selectedWriteUuid,
                _rxWriteType == CBCharacteristicWriteWithResponse ? "with-response writes" : "without-response writes",
                selectedWriteProps);
            if (rxChar == IntPtr.Zero)
                _logger.LogWarning("BLE: Using fallback write characteristic (NUS RX UUID not present)");
        }

        SendNotify(peripheral, SelSetNotifyValue, 1, txChar); // YES = 1
        IsConnected = true;
        _connectTcs?.TrySetResult(true);
        ConnectionStateChanged?.Invoke(this, true);
        _logger.LogInformation("BLE NUS TX characteristic subscribed, GPS data flowing");
    }

    private void OnDidUpdateValue(IntPtr characteristic)
    {
        var valueData = Send(characteristic, SelValue);
        if (valueData == IntPtr.Zero) return;

        var bytesPtr = Send(valueData, SelBytes);
        var length   = (int)SendUInt(valueData, SelLength);
        if (bytesPtr == IntPtr.Zero || length == 0) return;

        var chunk = new byte[length];
        Marshal.Copy(bytesPtr, chunk, 0, length);
        var text = Encoding.ASCII.GetString(chunk);

        _lineBuffer.Append(text);
        var buf = _lineBuffer.ToString();
        int nl;
        while ((nl = buf.IndexOf('\n')) >= 0)
        {
            var line = buf[..nl].TrimEnd('\r');
            buf = buf[(nl + 1)..];
            if (!string.IsNullOrWhiteSpace(line))
                NmeaLineReceived?.Invoke(this, line);
        }
        _lineBuffer.Clear();
        _lineBuffer.Append(buf);
    }

    private void OnDidDisconnect()
    {
        _logger.LogWarning("BLE peripheral disconnected");
        ReplaceConnectedPeripheral(IntPtr.Zero);
        ReplaceRxCharacteristic(IntPtr.Zero);
        _rxWriteType = CBCharacteristicWriteWithoutResponse;
        if (IsConnected)
        {
            IsConnected        = false;
            ConnectedDeviceName = null;
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    // ── Public IGpsBluetoothService ───────────────────────────────────────────

    public async Task<IList<string>> ScanForDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning) return Array.Empty<string>();
        IsScanning = true;
        ClearDiscoveredPeripherals();

        try
        {
            // Wait up to 3 s for BT to power on
            if (!_bleReady)
            {
                _powerOnTcs = new TaskCompletionSource<bool>();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(3000);
                try   { await _powerOnTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
                catch { /* timeout */ }
                _powerOnTcs = null;
            }

            if (!_bleReady)
            {
                _logger.LogWarning("CBCentralManager not powered on — Bluetooth may be off or app lacks permission. " +
                    "Go to System Settings → Privacy & Security → Bluetooth and allow this app.");
                return Array.Empty<string>();
            }

            // Scan for all nearby devices (nil services = no filter).
            // Many GPS receivers don't include the NUS service UUID in their advertisement
            // data, so a filtered scan would miss them entirely.
            Send2(_centralManager, SelScanForPeripherals, IntPtr.Zero, IntPtr.Zero);
            _logger.LogInformation("BLE scan started (all devices)");

            await Task.Delay(8000, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_centralManager != IntPtr.Zero)
                Send(_centralManager, SelStopScan);
            IsScanning = false;
            _logger.LogInformation("BLE scan done — found {Count} device(s)", _discovered.Count);
        }

        return new List<string>(_discovered.Keys);
    }

    public async Task<bool> ConnectAsync(string deviceName)
    {
        await DisconnectAsync().ConfigureAwait(false);

        if (!_discovered.TryGetValue(deviceName, out var peripheral))
        {
            _logger.LogWarning("BLE device '{Name}' not in scan results — scan first", deviceName);
            return false;
        }

        ReplaceConnectedPeripheral(peripheral);
        ConnectedDeviceName  = deviceName;
        _connectTcs          = new TaskCompletionSource<bool>();

        Send2(_centralManager, SelConnectPeripheral, peripheral, IntPtr.Zero);

        try
        {
            var ok = await _connectTcs.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _connectTcs = null;
            if (!ok) ConnectedDeviceName = null;
            return ok;
        }
        catch
        {
            _connectTcs        = null;
            ConnectedDeviceName = null;
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        if (_connectedPeripheral != IntPtr.Zero && _centralManager != IntPtr.Zero)
            Send1(_centralManager, SelCancelPeripheral, _connectedPeripheral);

        ReplaceConnectedPeripheral(IntPtr.Zero);
        ReplaceRxCharacteristic(IntPtr.Zero);
        _rxWriteType = CBCharacteristicWriteWithoutResponse;

        if (IsConnected)
        {
            IsConnected        = false;
            ConnectedDeviceName = null;
            ConnectionStateChanged?.Invoke(this, false);
        }
        return Task.CompletedTask;
    }

    public async Task<bool> WriteRtcmAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _connectedPeripheral == IntPtr.Zero || _rxCharacteristic == IntPtr.Zero || data.Length == 0)
            return false;

        var lockTaken = false;
        try
        {
            await _rtcmWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;

            var maxChunk = (int)SendLong1(_connectedPeripheral, SelMaximumWriteLenForType, _rxWriteType);
            if (maxChunk <= 0)
                maxChunk = _rxWriteType == CBCharacteristicWriteWithResponse ? 20 : 180;
            maxChunk = Math.Clamp(maxChunk, 20, 180);

            var offset = 0;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var len = Math.Min(maxChunk, data.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, offset, chunk, 0, len);

                var nsData = MakeNSData(chunk);
                Send3Long(_connectedPeripheral, SelWriteValueForCharType, nsData, _rxCharacteristic, _rxWriteType);
                objc_release(nsData);

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

    // ── ObjC helpers ──────────────────────────────────────────────────────────

    private static IntPtr MakeNSString(string s)
    {
        var cls   = objc_getClass("NSString");
        var alloc = Send(cls, SelAlloc);
        // initWithUTF8String: expects a null-terminated UTF-8 C string
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        var pin   = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var ns    = Send1(alloc, SelInitWithUTF8, pin.AddrOfPinnedObject());
        pin.Free();
        return ns;
    }

    private static IntPtr MakeCBUUID(string uuidString)
    {
        var cls   = objc_getClass("CBUUID");
        var nsStr = MakeNSString(uuidString);
        var uuid = Send1(cls, SelUUIDWithString, nsStr);
        if (nsStr != IntPtr.Zero) objc_release(nsStr);
        return uuid;
    }

    private static IntPtr MakeNSData(byte[] bytes)
    {
        var cls = objc_getClass("NSData");
        var alloc = Send(cls, SelAlloc);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var data = SendPtrUInt(alloc, SelInitWithBytesLength, pin.AddrOfPinnedObject(), (nuint)bytes.Length);
        pin.Free();
        return data;
    }

    private static IntPtr MakeNSArray(IntPtr[] objects)
    {
        var cls = objc_getClass("NSArray");
        var pin = GCHandle.Alloc(objects, GCHandleType.Pinned);
        var arr = SendArr(cls, SelArrayWithObjsCount, pin.AddrOfPinnedObject(), (nuint)objects.Length);
        pin.Free();
        return arr;
    }

    /// <summary>Returns the uppercase UUID string from a CBUUID instance.</summary>
    private static string GetCBUUIDString(IntPtr cbuuid)
    {
        if (cbuuid == IntPtr.Zero) return string.Empty;
        var nsStr = Send(cbuuid, SelUUIDString);       // CBUUID.UUIDString → NSString
        if (nsStr  == IntPtr.Zero) return string.Empty;
        var utf8  = Send(nsStr, SelUTF8String);         // NSString.UTF8String → const char*
        return utf8 == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(utf8) ?? string.Empty);
    }

    // kept for compatibility — same as GetCBUUIDString
    private static string GetUUIDString(IntPtr cbuuid) => GetCBUUIDString(cbuuid);

    private void RememberDiscoveredPeripheral(string name, IntPtr peripheral)
    {
        if (peripheral == IntPtr.Zero) return;

        var retained = objc_retain(peripheral);
        if (_discovered.TryGetValue(name, out var existing))
        {
            if (existing == retained)
            {
                objc_release(retained);
                return;
            }

            _discovered[name] = retained;
            if (existing != IntPtr.Zero) objc_release(existing);
            return;
        }

        _discovered[name] = retained;
    }

    private void ClearDiscoveredPeripherals()
    {
        foreach (var kv in _discovered.ToArray())
        {
            if (_discovered.TryRemove(kv.Key, out var ptr) && ptr != IntPtr.Zero)
                objc_release(ptr);
        }
    }

    private void ReplaceConnectedPeripheral(IntPtr peripheral)
    {
        var next = peripheral == IntPtr.Zero ? IntPtr.Zero : objc_retain(peripheral);
        var prev = _connectedPeripheral;
        _connectedPeripheral = next;
        if (prev != IntPtr.Zero) objc_release(prev);
    }

    private void ReplaceRxCharacteristic(IntPtr characteristic)
    {
        var next = characteristic == IntPtr.Zero ? IntPtr.Zero : objc_retain(characteristic);
        var prev = _rxCharacteristic;
        _rxCharacteristic = next;
        if (prev != IntPtr.Zero) objc_release(prev);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        ClearDiscoveredPeripherals();
        ReplaceRxCharacteristic(IntPtr.Zero);

        if (_delegateObj != IntPtr.Zero)
        {
            _instances.TryRemove(_delegateObj, out _);
            objc_release(_delegateObj);
            _delegateObj = IntPtr.Zero;
        }
        if (_centralManager != IntPtr.Zero)
        {
            objc_release(_centralManager);
            _centralManager = IntPtr.Zero;
        }
        if (_dispatchQueue != IntPtr.Zero)
        {
            dispatch_release(_dispatchQueue);
            _dispatchQueue = IntPtr.Zero;
        }
        _rtcmWriteLock.Dispose();
    }
}
