using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Gomoku.Services;
using Java.Util;
using Application = Android.App.Application;
using Context = Android.Content.Context;

namespace Gomoku.Platforms.Android.Services;

/// <summary>
/// 基于 Android 原生 BLE GATT 的蓝牙对战传输：
/// 主机（Host）广播自定义服务并启动 GATT Server；
/// 从机（Guest）扫描该服务并自动连接。
/// 双向通道：主机→从机用 Notify，从机→主机用 Write。
/// </summary>
public class BluetoothService : IBluetoothService
{
    // 自定义 GATT 服务 / 特征 / CCCD
    private static readonly UUID ServiceUuid = UUID.FromString("7d6b1a01-5f3e-4c8a-9e2d-8a1f6c0b3e11")!;
    private static readonly UUID CharUuid = UUID.FromString("7d6b1a02-5f3e-4c8a-9e2d-8a1f6c0b3e11")!;
    private static readonly UUID CccdUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private readonly Context _context;
    private BluetoothManager? _manager;
    private BluetoothAdapter? _adapter;

    // 主机侧
    private BluetoothGattServer? _gattServer;
    private BluetoothLeAdvertiser? _advertiser;
    private AdvertiseCallback? _advCallback;
    private BluetoothDevice? _hostDevice;
    private BluetoothGattCharacteristic? _hostChar;

    // 从机侧
    private BluetoothLeScanner? _scanner;
    private ScanCallback? _scanCallback;
    private BluetoothGatt? _gatt;
    private BluetoothGattCharacteristic? _guestChar;
    private bool _guestReady;

    public bool IsConnected { get; private set; }
    public BluetoothRole Role { get; private set; } = BluetoothRole.None;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    public BluetoothService()
    {
        _context = Application.Context;
        _manager = _context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        _adapter = _manager?.Adapter;
    }

    // ---------------------------------------------------------------- 权限

    private class BtPermissions : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions
        {
            get
            {
                var list = new List<(string, bool)>();
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                {
                    list.Add(("android.permission.BLUETOOTH_SCAN", true));
                    list.Add(("android.permission.BLUETOOTH_CONNECT", true));
                    list.Add(("android.permission.BLUETOOTH_ADVERTISE", true));
                }
                else
                {
                    list.Add(("android.permission.BLUETOOTH", false));
                    list.Add(("android.permission.BLUETOOTH_ADMIN", false));
                    list.Add(("android.permission.ACCESS_FINE_LOCATION", true));
                }
                return list.ToArray();
            }
        }
    }

    private async Task<bool> EnsureReadyAsync(string roleText)
    {
        var status = await Permissions.RequestAsync<BtPermissions>();
        if (status != PermissionStatus.Granted)
        {
            RaiseStatus($"需要蓝牙权限才能{roleText}");
            return false;
        }

        _manager = _context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        _adapter = _manager?.Adapter;
        if (_adapter is null || !_adapter.IsEnabled)
        {
            RaiseStatus("请先开启手机蓝牙，然后重试");
            TryPromptEnableBluetooth();
            return false;
        }
        return true;
    }

    private void TryPromptEnableBluetooth()
    {
        try
        {
            var intent = new Intent(BluetoothAdapter.ActionRequestEnable);
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
        }
        catch { /* 部分机型不允许弹窗，用户需手动开蓝牙 */ }
    }

    // ---------------------------------------------------------------- 主机

    public async Task<bool> StartHostingAsync()
    {
        if (!await EnsureReadyAsync("创建房间")) return false;

        Stop();
        Role = BluetoothRole.Host;

        var serverCallback = new ServerGattCallback(this);
        _gattServer = _manager!.OpenGattServer(_context, serverCallback);
        if (_gattServer is null)
        {
            RaiseStatus("无法启动蓝牙服务，请重试");
            return false;
        }

        var service = new BluetoothGattService(ServiceUuid, GattServiceType.Primary);
        var ch = new BluetoothGattCharacteristic(CharUuid,
            GattProperty.Write | GattProperty.Notify,
            GattPermission.Write | GattPermission.Read);
        var cccd = new BluetoothGattDescriptor(CccdUuid,
            GattDescriptorPermission.Read | GattDescriptorPermission.Write);
        ch.AddDescriptor(cccd);
        service.AddCharacteristic(ch);

        if (!_gattServer.AddService(service))
        {
            RaiseStatus("GATT 服务添加失败");
            return false;
        }
        _hostChar = ch;
        // AddService 完成回调（OnServiceAdded）后开始广播
        serverCallback.ServiceAdded = () => StartAdvertising();
        return true;
    }

    private void StartAdvertising()
    {
        try
        {
            _advertiser = _adapter!.BluetoothLeAdvertiser;
            if (_advertiser is null)
            {
                RaiseStatus("本机不支持 BLE 广播");
                return;
            }

            var settings = new AdvertiseSettings.Builder()
                .SetAdvertiseMode(AdvertiseMode.LowLatency)
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)
                .SetConnectable(true)
                .Build();

            var data = new AdvertiseData.Builder()
                .SetIncludeDeviceName(false)
                .AddServiceUuid(new ParcelUuid(ServiceUuid))
                .Build();

            _advCallback = new HostAdvCallback(this);
            _advertiser.StartAdvertising(settings, data, _advCallback);
            RaiseStatus("正在创建房间…");
        }
        catch (Exception ex)
        {
            RaiseStatus($"广播异常：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 从机

    public async Task<bool> StartJoiningAsync()
    {
        if (!await EnsureReadyAsync("加入房间")) return false;

        Stop();
        Role = BluetoothRole.Guest;
        _guestReady = false;

        try
        {
            _scanner = _adapter!.BluetoothLeScanner;
            if (_scanner is null)
            {
                RaiseStatus("本机不支持 BLE 扫描");
                return false;
            }

            var filter = new ScanFilter.Builder()!
                .SetServiceUuid(new ParcelUuid(ServiceUuid)!)!
                .Build()!;
            var settings = new ScanSettings.Builder()!
                .SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!
                .Build()!;

            _scanCallback = new GuestScanCallback(this);
            _scanner.StartScan(new List<ScanFilter> { filter! }, settings, _scanCallback);
            RaiseStatus("正在搜索附近的房间，请将两台手机靠近…");
            return true;
        }
        catch (Exception ex)
        {
            RaiseStatus($"扫描异常：{ex.Message}");
            return false;
        }
    }

    private void OnDeviceFound(BluetoothDevice device)
    {
        try
        {
            _scanner?.StopScan(_scanCallback);
        }
        catch { }
        RaiseStatus("已发现房间，正在连接…");

        var gattCallback = new ClientGattCallback(this);
#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            _gatt = device.ConnectGatt(_context, false, gattCallback, BluetoothTransports.Le);
        else
            _gatt = device.ConnectGatt(_context, false, gattCallback);
#pragma warning restore CA1416
    }

    // ---------------------------------------------------------------- 收发

    public async Task SendAsync(string message)
    {
        if (!IsConnected) return;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);

        if (Role == BluetoothRole.Host)
        {
            if (_gattServer is null || _hostDevice is null || _hostChar is null) return;
            await Task.Run(() =>
            {
                try
                {
                    bool ok;
#pragma warning disable CA1416
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                    {
                        _gattServer.NotifyCharacteristicChanged(_hostDevice, _hostChar, false, bytes);
                        ok = true;
                    }
                    else
                    {
#pragma warning disable CA1422
                        _hostChar.SetValue(bytes);
                        ok = _gattServer.NotifyCharacteristicChanged(_hostDevice, _hostChar, false);
#pragma warning restore CA1422
                    }
#pragma warning restore CA1416
                    if (!ok) RaiseStatus("发送失败");
                }
                catch (Exception ex) { RaiseStatus($"发送异常：{ex.Message}"); }
            });
        }
        else if (Role == BluetoothRole.Guest)
        {
            if (_gatt is null || _guestChar is null || !_guestReady) return;
            await Task.Run(() =>
            {
                try
                {
#pragma warning disable CA1416
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                        _gatt.WriteCharacteristic(_guestChar, bytes, (int)GattWriteType.Default);
                    else
                    {
#pragma warning disable CA1422
                        _guestChar.SetValue(bytes);
                        _gatt.WriteCharacteristic(_guestChar);
#pragma warning restore CA1422
                    }
#pragma warning restore CA1416
                }
                catch (Exception ex) { RaiseStatus($"发送异常：{ex.Message}"); }
            });
        }
    }

    private void OnReceive(byte[]? value)
    {
        if (value is null) return;
        string msg = System.Text.Encoding.UTF8.GetString(value);
        MainThread.BeginInvokeOnMainThread(() => MessageReceived?.Invoke(this, msg));
    }

    // ---------------------------------------------------------------- 连接状态

    private void OnConnected()
    {
        if (IsConnected) return;
        IsConnected = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusChanged?.Invoke(this, "已连接，开始对战！");
            Connected?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>从机侧：CCCD 订阅发出后调用，标记通道就绪并触发连接事件。</summary>
    internal void MarkGuestReady()
    {
        _guestReady = true;
        OnConnected();
    }

    private void OnDisconnected()
    {
        if (!IsConnected && Role == BluetoothRole.None) return;
        IsConnected = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusChanged?.Invoke(this, "蓝牙连接已断开");
            Disconnected?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RaiseStatus(string text) =>
        MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(this, text));

    // ---------------------------------------------------------------- 清理

    public void Stop()
    {
        IsConnected = false;
        Role = BluetoothRole.None;
        _hostDevice = null;
        _hostChar = null;
        _guestChar = null;
        _guestReady = false;

        try { if (_advertiser is not null && _advCallback is not null) _advertiser.StopAdvertising(_advCallback); } catch { }
        _advCallback = null;
        try { _scanner?.StopScan(_scanCallback); } catch { }
        _scanCallback = null;
        try { _gattServer?.Close(); } catch { }
        _gattServer = null;
        try { _gatt?.Disconnect(); } catch { }
        try { _gatt?.Close(); } catch { }
        _gatt = null;
    }

    // ================================================================
    //  GATT Server 回调（主机）
    // ================================================================
    private class ServerGattCallback : BluetoothGattServerCallback
    {
        private readonly BluetoothService _svc;
        public Action? ServiceAdded;

        public ServerGattCallback(BluetoothService svc) => _svc = svc;

        public override void OnServiceAdded(GattStatus status, BluetoothGattService? service)
        {
            if (status == GattStatus.Success) ServiceAdded?.Invoke();
            else _svc.RaiseStatus($"GATT 服务添加失败（{status}）");
        }

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            if (newState == ProfileState.Connected)
            {
                _svc._hostDevice = device;
                // 等从机打开通知（OnDescriptorWriteRequest）后再视为连通并发送握手
            }
            else if (newState == ProfileState.Disconnected)
            {
                _svc._hostDevice = null;
                _svc.OnDisconnected();
            }
        }

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattCharacteristic? characteristic, bool preparedWrite, bool responseNeeded,
            int offset, byte[]? value)
        {
            if (responseNeeded && device is not null)
                _svc._gattServer?.SendResponse(device, requestId, GattStatus.Success, offset, value ?? Array.Empty<byte>());
            _svc.OnReceive(value);
        }

        public override void OnDescriptorWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattDescriptor? descriptor, bool preparedWrite, bool responseNeeded,
            int offset, byte[]? value)
        {
            if (responseNeeded && device is not null)
                _svc._gattServer?.SendResponse(device, requestId, GattStatus.Success, offset, value ?? Array.Empty<byte>());

            // 从机已开启 Notify，通道双向就绪
            if (descriptor?.Uuid?.Equals(CccdUuid) == true && _svc.Role == BluetoothRole.Host)
            {
                _ = _svc.SendAsync("S");
                _svc.OnConnected();
            }
        }
    }

    // ================================================================
    //  GATT Client 回调（从机）
    // ================================================================
    private class ClientGattCallback : BluetoothGattCallback
    {
        private readonly BluetoothService _svc;
        public ClientGattCallback(BluetoothService svc) => _svc = svc;

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected)
            {
                gatt?.DiscoverServices();
            }
            else if (newState == ProfileState.Disconnected)
            {
                _svc._guestReady = false;
                _svc.OnDisconnected();
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            var ch = gatt?.GetService(ServiceUuid)?.GetCharacteristic(CharUuid);
            if (gatt is null || ch is null)
            {
                _svc.RaiseStatus("连接失败：未找到对战服务");
                return;
            }
            _svc._guestChar = ch;

            try
            {
                gatt.SetCharacteristicNotification(ch, true);
                var descriptor = ch.GetDescriptor(CccdUuid);
                if (descriptor is not null)
                {
                    byte[] value = BluetoothGattDescriptor.EnableNotificationValue!.ToArray();
#pragma warning disable CA1416
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                        gatt.WriteDescriptor(descriptor, value);
                    else
                    {
#pragma warning disable CA1422
                        descriptor.SetValue(value);
                        gatt.WriteDescriptor(descriptor);
#pragma warning restore CA1422
                    }
#pragma warning restore CA1416
                }
            }
            catch (Exception ex)
            {
                _svc.RaiseStatus($"订阅通知失败：{ex.Message}");
            }

            // CCCD 写入已发出即视为通道就绪（主机侧以收到 CCCD 写入为准，故此处提前无害）
            _svc.MarkGuestReady();
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            if (descriptor?.Uuid?.Equals(CccdUuid) == true && status == GattStatus.Success)
            {
                _svc._guestReady = true;
                _svc.OnConnected();
            }
        }

        // API 33 以上走 value 参数重载；低版本走 characteristic.GetValue()
        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, byte[]? value)
            => _svc.OnReceive(value);

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
#pragma warning disable CA1422, CA1416
            _svc.OnReceive(characteristic?.GetValue());
#pragma warning restore CA1422, CA1416
        }
    }

    // ================================================================
    //  广播回调（主机）
    // ================================================================
    private class HostAdvCallback : AdvertiseCallback
    {
        private readonly BluetoothService _svc;
        public HostAdvCallback(BluetoothService svc) => _svc = svc;
        public override void OnStartSuccess(AdvertiseSettings? settingsInEffect)
            => _svc.RaiseStatus("房间已创建，等待对方加入…");
        public override void OnStartFailure(AdvertiseFailure errorCode)
            => _svc.RaiseStatus($"广播启动失败（{errorCode}），请重试");
    }

    // ================================================================
    //  扫描回调（从机）
    // ================================================================
    private class GuestScanCallback : ScanCallback
    {
        private readonly BluetoothService _svc;
        public GuestScanCallback(BluetoothService svc) => _svc = svc;
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is null) return;
            _svc.OnDeviceFound(result.Device);
        }
        public override void OnScanFailed(ScanFailure errorCode)
            => _svc.RaiseStatus($"扫描失败（{errorCode}），请重试");
    }
}
