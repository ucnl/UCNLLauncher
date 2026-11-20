using System.Threading.Channels;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using AndroidX.Core.Content;
using Hoho.Android.UsbSerial.Driver;

namespace UCNLLauncher.Services;

public class UsbPortInfo
{
    public IUsbSerialPort Port { get; set; } = null!;
    public UsbDeviceConnection Connection { get; set; } = null!;
    public UsbDevice Device { get; set; } = null!;
    public int BaudRate { get; set; }
}

public class UsbService
{
    private const string PermissionAction = "com.unavlab.ucnllauncher.USB_PERMISSION";
    private const int DefaultBaudRate = 9600;
    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromSeconds(60);
    private const int WriteWaitMs = 3000;
    private const int WriteWaitPollMs = 50;
    private const int MaxPendingBytesWhileClosed = 64 * 1024;

    private sealed class PortWriter
    {
        public readonly Channel<byte[]> Channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        public int PendingBytes;
        public volatile bool Closed;
    }

    private readonly Context _context;
    private readonly UsbManager _usbManager;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, UsbPortInfo> _ports = new();
    private readonly Dictionary<int, int> _desiredBaud = new();
    private readonly Dictionary<int, string> _lastDevice = new();
    private readonly Dictionary<int, PortWriter> _writers = new();
    private readonly HashSet<int> _openRequests = new();
    private readonly HashSet<string> _deniedDevices = new();
    private readonly UsbPermissionReceiver _permissionReceiver;
    private TaskCompletionSource<bool>? _permissionTcs;
    private bool _receiverRegistered;

    public UsbService()
    {
        _context = Android.App.Application.Context;
        _usbManager = _context.GetSystemService(Context.UsbService) as UsbManager
            ?? throw new Exception("USB Service not available");
        _permissionReceiver = new UsbPermissionReceiver(OnPermissionResult);
    }

    public bool IsPortOpen(int portId)
    {
        lock (_sync)
            return _ports.ContainsKey(portId);
    }

    public bool IsAnyPortOpen
    {
        get
        {
            lock (_sync)
                return _ports.Count > 0;
        }
    }

    public async Task<bool> TryConnectAsync(int portId = 0, int baudRate = DefaultBaudRate, bool userInitiated = false)
    {
        lock (_sync)
            _desiredBaud[portId] = baudRate;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(portId, baudRate, null, userInitiated, false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetBaudAsync(int portId, int baudRate)
    {
        lock (_sync)
            _desiredBaud[portId] = baudRate;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int targetBaud;
            string? preferred;
            lock (_sync)
            {
                targetBaud = _desiredBaud.TryGetValue(portId, out var b) ? b : baudRate;
                preferred = _ports.TryGetValue(portId, out var current)
                    ? current.Device.DeviceName
                    : _lastDevice.GetValueOrDefault(portId);
            }

            ClosePortCore(portId);
            await Task.Delay(300).ConfigureAwait(false);
            return await ConnectCoreAsync(portId, targetBaud, preferred, false, true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(int portId)
    {
        lock (_sync)
            _desiredBaud[portId] = DefaultBaudRate;
        DiscardWriter(portId);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ClosePortCore(portId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> EnsureOpenAsync(int portId)
    {
        lock (_sync)
        {
            if (_ports.ContainsKey(portId)) return true;
            if (!_desiredBaud.ContainsKey(portId)) return false;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int baud;
            string? preferred;
            lock (_sync)
            {
                if (_ports.ContainsKey(portId)) return true;
                if (!_desiredBaud.TryGetValue(portId, out baud)) return false;
                preferred = _lastDevice.GetValueOrDefault(portId);
            }
            return await ConnectCoreAsync(portId, baud, preferred, false, true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool CloseDetachedPorts()
    {
        HashSet<string> attached;
        try
        {
            attached = new HashSet<string>(_usbManager.DeviceList?.Keys ?? (ICollection<string>)Array.Empty<string>());
        }
        catch
        {
            return false;
        }

        List<(int, UsbPortInfo)> detached;
        lock (_sync)
        {
            detached = _ports
                .Where(kv => !attached.Contains(kv.Value.Device.DeviceName))
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        foreach (var (portId, info) in detached)
            CloseIfCurrent(portId, info);

        return detached.Count > 0;
    }

    private async Task<bool> ConnectCoreAsync(int portId, int baudRate, string? preferredDevice, bool userInitiated, bool respectReservations)
    {
        ClosePortCore(portId);

        IEnumerable<UsbDevice> devices;
        try
        {
            devices = _usbManager.DeviceList?.Values?.ToList() ?? new List<UsbDevice>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"USB error (port {portId}): {ex.Message}");
            return false;
        }

        if (preferredDevice != null)
        {
            var preferred = devices.FirstOrDefault(d => d.DeviceName == preferredDevice);
            if (preferred != null)
                devices = new[] { preferred };
        }

        foreach (var device in devices)
        {
            lock (_sync)
            {
                if (_ports.Values.Any(p => p.Device.DeviceId == device.DeviceId))
                    continue;
                if (respectReservations && IsReservedByOtherPort(portId, device.DeviceName))
                    continue;
            }

            if (await TryOpenDeviceAsync(portId, device, baudRate, userInitiated).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private bool IsReservedByOtherPort(int portId, string deviceName) =>
        _lastDevice.Any(kv => kv.Key != portId && kv.Value == deviceName);

    private async Task<bool> TryOpenDeviceAsync(int portId, UsbDevice device, int baudRate, bool userInitiated)
    {
        UsbDeviceConnection? connection = null;
        IUsbSerialPort? port = null;
        try
        {
            var driver = UsbSerialProber.DefaultProber.ProbeDevice(device);
            if (driver == null) return false;

            if (!_usbManager.HasPermission(device))
            {
                bool denied;
                lock (_sync)
                    denied = _deniedDevices.Contains(device.DeviceName);
                if (denied && !userInitiated) return false;

                var granted = await RequestPermissionAsync(device).ConfigureAwait(false);
                lock (_sync)
                {
                    if (granted) _deniedDevices.Remove(device.DeviceName);
                    else _deniedDevices.Add(device.DeviceName);
                }
                if (!granted) return false;
            }

            var ports = driver.Ports;
            if (ports == null || ports.Count == 0) return false;

            port = ports[0];
            if (port == null) return false;

            connection = _usbManager.OpenDevice(device);
            if (connection == null) return false;

            port.Open(connection);
            port.SetParameters(baudRate, 8, StopBits.One, Parity.None);

            lock (_sync)
            {
                _ports[portId] = new UsbPortInfo
                {
                    Port = port,
                    Connection = connection,
                    Device = device,
                    BaudRate = baudRate
                };
                foreach (var other in _lastDevice.Where(kv => kv.Key != portId && kv.Value == device.DeviceName).Select(kv => kv.Key).ToList())
                    _lastDevice.Remove(other);
                _lastDevice[portId] = device.DeviceName;
            }

            System.Diagnostics.Debug.WriteLine($"Connected port {portId}: VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4} @ {baudRate}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"USB error (port {portId}): {ex.Message}");
            ReleasePort(port, connection);
            return false;
        }
    }

    private Task<bool> RequestPermissionAsync(UsbDevice device)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_sync)
            {
                _permissionTcs?.TrySetResult(false);
                _permissionTcs = tcs;

                if (!_receiverRegistered)
                {
                    ContextCompat.RegisterReceiver(_context, _permissionReceiver,
                        new IntentFilter(PermissionAction), ContextCompat.ReceiverNotExported);
                    _receiverRegistered = true;
                }
            }

            var intent = new Intent(PermissionAction);
            intent.SetPackage(_context.PackageName);

            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                flags |= PendingIntentFlags.Mutable;

            var pendingIntent = PendingIntent.GetBroadcast(_context, 0, intent, flags);
            _usbManager.RequestPermission(device, pendingIntent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"USB permission error: {ex.Message}");
            tcs.TrySetResult(false);
        }

        return WaitPermissionAsync(tcs, device);
    }

    private async Task<bool> WaitPermissionAsync(TaskCompletionSource<bool> tcs, UsbDevice device)
    {
        await Task.WhenAny(tcs.Task, Task.Delay(PermissionTimeout)).ConfigureAwait(false);

        lock (_sync)
        {
            if (ReferenceEquals(_permissionTcs, tcs))
                _permissionTcs = null;
        }

        try
        {
            return _usbManager.HasPermission(device);
        }
        catch
        {
            return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
        }
    }

    private void OnPermissionResult(bool granted)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_sync)
            tcs = _permissionTcs;
        tcs?.TrySetResult(granted);
    }

    public async Task<byte[]?> ReadAsync(int portId, int timeoutMs = 1000)
    {
        UsbPortInfo? p;
        lock (_sync)
            _ports.TryGetValue(portId, out p);
        if (p == null) return null;

        try
        {
            var buffer = new byte[1024];
            var result = await Task.Run(() => p.Port.Read(buffer, timeoutMs)).ConfigureAwait(false);
            if (result > 0)
                return System.Text.Encoding.ASCII.GetBytes(System.Text.Encoding.ASCII.GetString(buffer, 0, result));
        }
        catch (Exception ex) when (ex is Java.IO.IOException or ObjectDisposedException or NullReferenceException)
        {
            CloseIfCurrent(portId, p);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Read error (port {portId}): {ex.Message}");
        }
        return null;
    }

    public bool Write(int portId, string data)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(data);
        bool open;
        lock (_sync)
        {
            open = _ports.ContainsKey(portId);
            if (!open && !_desiredBaud.ContainsKey(portId)) return false;

            if (!_writers.TryGetValue(portId, out var writer) || writer.Closed)
            {
                writer = new PortWriter();
                _writers[portId] = writer;
                _ = Task.Run(() => WriteLoopAsync(portId, writer));
            }

            if (!open && Volatile.Read(ref writer.PendingBytes) + bytes.Length > MaxPendingBytesWhileClosed)
                return false;

            Interlocked.Add(ref writer.PendingBytes, bytes.Length);
            if (!writer.Channel.Writer.TryWrite(bytes))
            {
                Interlocked.Add(ref writer.PendingBytes, -bytes.Length);
                return false;
            }
        }

        if (!open)
            RequestOpen(portId);
        return true;
    }

    private void RequestOpen(int portId)
    {
        lock (_sync)
        {
            if (!_openRequests.Add(portId)) return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureOpenAsync(portId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Open request error (port {portId}): {ex.Message}");
            }
            finally
            {
                lock (_sync)
                    _openRequests.Remove(portId);
            }
        });
    }

    private async Task<UsbPortInfo?> WaitForPortAsync(int portId, PortWriter writer)
    {
        var deadline = Environment.TickCount64 + WriteWaitMs;
        bool requested = false;
        while (true)
        {
            lock (_sync)
            {
                if (writer.Closed) return null;
                if (_ports.TryGetValue(portId, out var p)) return p;
                if (!_desiredBaud.ContainsKey(portId)) return null;
            }

            if (!requested)
            {
                requested = true;
                RequestOpen(portId);
            }

            if (Environment.TickCount64 >= deadline) return null;
            await Task.Delay(WriteWaitPollMs).ConfigureAwait(false);
        }
    }

    private static void DiscardPending(PortWriter writer)
    {
        while (writer.Channel.Reader.TryRead(out var dropped))
            Interlocked.Add(ref writer.PendingBytes, -dropped.Length);
    }

    private List<PortWriter> DetachWritersLocked()
    {
        var writers = _writers.Values.ToList();
        foreach (var writer in writers)
            writer.Closed = true;
        _writers.Clear();
        return writers;
    }

    public void DiscardPendingWrites()
    {
        List<PortWriter> writers;
        lock (_sync)
            writers = DetachWritersLocked();

        foreach (var writer in writers)
            writer.Channel.Writer.TryComplete();
    }

    private async Task WriteLoopAsync(int portId, PortWriter writer)
    {
        var reader = writer.Channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var bytes))
            {
                Interlocked.Add(ref writer.PendingBytes, -bytes.Length);
                if (writer.Closed) continue;

                var p = await WaitForPortAsync(portId, writer).ConfigureAwait(false);
                if (p == null)
                {
                    DiscardPending(writer);
                    continue;
                }

                try
                {
                    p.Port.Write(bytes, 1000);
                }
                catch (Java.IO.IOException)
                {
                    CloseIfCurrent(portId, p);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Write error (port {portId}): {ex.Message}");
                }
            }
        }
    }

    private void DiscardWriter(int portId)
    {
        PortWriter? writer;
        lock (_sync)
        {
            if (!_writers.Remove(portId, out writer)) return;
            writer.Closed = true;
        }
        writer.Channel.Writer.TryComplete();
    }

    public void CloseAll()
    {
        List<UsbPortInfo> all;
        List<PortWriter> writers;
        lock (_sync)
        {
            all = _ports.Values.ToList();
            writers = DetachWritersLocked();
            _ports.Clear();
            _desiredBaud.Clear();
            _lastDevice.Clear();
        }

        foreach (var writer in writers)
            writer.Channel.Writer.TryComplete();

        foreach (var p in all)
            ReleasePort(p.Port, p.Connection);
    }

    private void ClosePortCore(int portId)
    {
        UsbPortInfo? p;
        lock (_sync)
        {
            if (!_ports.Remove(portId, out p)) return;
        }
        ReleasePort(p.Port, p.Connection);
    }

    private void CloseIfCurrent(int portId, UsbPortInfo info)
    {
        lock (_sync)
        {
            if (!_ports.TryGetValue(portId, out var current) || !ReferenceEquals(current, info)) return;
            _ports.Remove(portId);
        }
        ReleasePort(info.Port, info.Connection);
    }

    private static void ReleasePort(IUsbSerialPort? port, UsbDeviceConnection? connection)
    {
        try
        {
            port?.Close();
        }
        catch { }

        try { (port as IDisposable)?.Dispose(); } catch { }
        try { connection?.Close(); } catch { }
    }
}

public class UsbPermissionReceiver : Android.Content.BroadcastReceiver
{
    private readonly Action<bool> _callback;
    public UsbPermissionReceiver(Action<bool> callback) => _callback = callback;

    public override void OnReceive(Context? context, Intent? intent)
    {
        var granted = intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) ?? false;
        _callback(granted);
    }
}
