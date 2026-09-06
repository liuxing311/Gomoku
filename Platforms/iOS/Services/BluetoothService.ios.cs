using CoreBluetooth;
using CoreFoundation;
using Foundation;
using Gomoku.Services;

namespace Gomoku.Platforms.iOS.Services;

/// <summary>
/// 基于 iOS CoreBluetooth 的蓝牙对战传输，与 Android 端 BLE GATT 协议完全对齐：
/// 主机（Host）= CBPeripheralManager（GATT Server + 广播），执黑先行；
/// 从机（Guest）= CBCentralManager（扫描 + GATT Client），执白。
/// 双向通道：主机→从机用 Notify，从机→主机用 Write（WithResponse）。
/// </summary>
public class BluetoothService : IBluetoothService
{
    // 与 Android 端完全一致的 GATT 标识
    private static readonly CBUUID ServiceUuid = CBUUID.FromString("7d6b1a01-5f3e-4c8a-9e2d-8a1f6c0b3e11")!;
    private static readonly CBUUID CharUuid = CBUUID.FromString("7d6b1a02-5f3e-4c8a-9e2d-8a1f6c0b3e11")!;

    // 主机侧（外设 / GATT Server）
    private PeripheralManagerDelegate? _pmDelegate;
    private CBPeripheralManager? _pm;
    private CBMutableCharacteristic? _hostChar;

    // 从机侧（中心 / GATT Client）
    private CentralManagerDelegate? _cmDelegate;
    private CBCentralManager? _cm;
    private CBPeripheral? _peripheral;
    private CBCharacteristic? _guestChar;
    private bool _guestReady;

    public bool IsConnected { get; private set; }
    public BluetoothRole Role { get; private set; } = BluetoothRole.None;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    // ---------------------------------------------------------------- 主机

    public Task<bool> StartHostingAsync()
    {
        Stop();
        Role = BluetoothRole.Host;
        _pmDelegate = new PeripheralManagerDelegate(this);
        // 初始化后 StateUpdated 回调中（PoweredOn）自动添加服务并广播
        _pm = new CBPeripheralManager(_pmDelegate, DispatchQueue.MainQueue);
        RaiseStatus("正在创建房间…");
        return Task.FromResult(true);
    }

    // ---------------------------------------------------------------- 从机

    public Task<bool> StartJoiningAsync()
    {
        Stop();
        Role = BluetoothRole.Guest;
        _guestReady = false;
        _cmDelegate = new CentralManagerDelegate(this);
        // 初始化后 UpdatedState 回调中（PoweredOn）自动开始扫描
        _cm = new CBCentralManager(_cmDelegate, DispatchQueue.MainQueue);
        RaiseStatus("正在搜索附近的房间，请将两台手机靠近…");
        return Task.FromResult(true);
    }

    // ---------------------------------------------------------------- 收发

    public Task SendAsync(string message)
    {
        if (!IsConnected) return Task.CompletedTask;
        var data = NSData.FromArray(System.Text.Encoding.UTF8.GetBytes(message));

        if (Role == BluetoothRole.Host)
        {
            if (_pm is null || _hostChar is null) return Task.CompletedTask;
            // 第三个参数为订阅的 central，null 表示发给所有订阅者
            _pm.UpdateValue(data, _hostChar, null);
        }
        else if (Role == BluetoothRole.Guest)
        {
            if (_peripheral is null || _guestChar is null || !_guestReady) return Task.CompletedTask;
            // WithResponse 对应 Android GATT Server 的 OnCharacteristicWriteRequest
            _peripheral.WriteValue(data, _guestChar, CBCharacteristicWriteType.WithResponse);
        }
        return Task.CompletedTask;
    }

    private void OnReceive(NSData? value)
    {
        if (value is null) return;
        var msg = System.Text.Encoding.UTF8.GetString(value.ToArray());
        if (!string.IsNullOrEmpty(msg))
            MainThread.BeginInvokeOnMainThread(() => MessageReceived?.Invoke(this, msg));
    }

    // ---------------------------------------------------------------- 连接状态

    internal void OnConnected()
    {
        if (IsConnected) return;
        IsConnected = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusChanged?.Invoke(this, "已连接，开始对战！");
            Connected?.Invoke(this, EventArgs.Empty);
        });
    }

    internal void OnDisconnected()
    {
        if (!IsConnected && Role == BluetoothRole.None) return;
        IsConnected = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusChanged?.Invoke(this, "蓝牙连接已断开");
            Disconnected?.Invoke(this, EventArgs.Empty);
        });
    }

    internal void RaiseStatus(string text) =>
        MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(this, text));

    // ---------------------------------------------------------------- 清理

    public void Stop()
    {
        IsConnected = false;
        Role = BluetoothRole.None;
        _hostChar = null;
        _guestChar = null;
        _guestReady = false;

        try { _pm?.StopAdvertising(); } catch { }
        try { _pm?.RemoveAllServices(); } catch { }
        try { if (_cm is not null && _peripheral is not null) _cm.CancelPeripheralConnection(_peripheral); } catch { }
        try { _cm?.StopScan(); } catch { }

        _peripheral = null;
        _pm = null;
        _cm = null;
        _pmDelegate = null;
        _cmDelegate = null;
    }

    // ================================================================
    //  主机回调：CBPeripheralManagerDelegate（GATT Server + 广播）
    // ================================================================
    private class PeripheralManagerDelegate : CBPeripheralManagerDelegate
    {
        private readonly BluetoothService _svc;
        public PeripheralManagerDelegate(BluetoothService svc) => _svc = svc;

        public override void StateUpdated(CBPeripheralManager peripheral)
        {
            switch (peripheral.State)
            {
                case CBPeripheralManagerState.PoweredOn:
                    // 特征：Notify（主机→从机）+ Write（从机→主机），与 Android 端一致
                    var ch = new CBMutableCharacteristic(
                        CharUuid,
                        CBCharacteristicProperty.Notify | CBCharacteristicProperty.Write,
                        CBAttributePermissions.Readable | CBAttributePermissions.Writeable);
                    var svc = new CBMutableService(ServiceUuid, true)
                    {
                        Characteristics = new CBCharacteristic[] { ch }
                    };
                    _svc._hostChar = ch;
                    peripheral.AddService(svc);
                    break;
                case CBPeripheralManagerState.PoweredOff:
                    _svc.RaiseStatus("请先开启手机蓝牙，然后重试");
                    break;
                case CBPeripheralManagerState.Unauthorized:
                    _svc.RaiseStatus("请在系统设置中允许本应用使用蓝牙");
                    break;
                case CBPeripheralManagerState.Unsupported:
                    _svc.RaiseStatus("本机不支持蓝牙低功耗");
                    break;
            }
        }

        public override void ServiceAdded(CBPeripheralManager peripheral, CBMutableService service, NSError? error)
        {
            if (error is not null)
            {
                _svc.RaiseStatus("GATT 服务添加失败");
                return;
            }
            // 广播自定义服务 UUID，Android 端扫描过滤器据此发现
            peripheral.StartAdvertising(new NSDictionary(
                CBAdvertisement.DataServiceUUIDsKey,
                NSArray.FromObjects(ServiceUuid)));
        }

        public override void AdvertisingStarted(CBPeripheralManager peripheral, NSError? error)
        {
            if (error is not null)
                _svc.RaiseStatus("广播启动失败，请重试");
            else
                _svc.RaiseStatus("房间已创建，等待对方加入…");
        }

        // 从机写入 CCCD 开启通知 → 通道双向就绪，发送握手
        public override void DidSubscribeToCharacteristic(CBPeripheralManager peripheral, CBCentral central, CBCharacteristic characteristic)
        {
            _svc.OnConnected();
            _ = _svc.SendAsync("S");
        }

        public override void DidUnsubscribeFromCharacteristic(CBPeripheralManager peripheral, CBCentral central, CBCharacteristic characteristic)
            => _svc.OnDisconnected();

        // 从机写特征（落子/重开消息）
        public override void WriteRequestsReceived(CBPeripheralManager peripheral, CBATTRequest[] requests)
        {
            foreach (var req in requests)
            {
                if (req.Characteristic?.UUID?.Equals(CharUuid) == true)
                    _svc.OnReceive(req.Value);
                // Write WithResponse 必须回复，否则 Android 端 GATT 写操作超时
                peripheral.RespondToRequest(req, CBATTError.Success);
            }
        }
    }

    // ================================================================
    //  从机回调：CBCentralManagerDelegate（扫描 + 连接管理）
    // ================================================================
    private class CentralManagerDelegate : CBCentralManagerDelegate
    {
        private readonly BluetoothService _svc;
        private PeripheralDelegate? _peripheralDelegate;

        public CentralManagerDelegate(BluetoothService svc) => _svc = svc;

        public override void UpdatedState(CBCentralManager central)
        {
            switch (central.State)
            {
                case CBManagerState.PoweredOn:
                    // 按服务 UUID 扫描，可发现 Android 主机的广播
                    central.ScanForPeripherals(new CBUUID[] { ServiceUuid });
                    break;
                case CBManagerState.PoweredOff:
                    _svc.RaiseStatus("请先开启手机蓝牙，然后重试");
                    break;
                case CBManagerState.Unauthorized:
                    _svc.RaiseStatus("请在系统设置中允许本应用使用蓝牙");
                    break;
                case CBManagerState.Unsupported:
                    _svc.RaiseStatus("本机不支持蓝牙低功耗");
                    break;
            }
        }

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSDictionary? advertisementData, NSNumber? RSSI)
        {
            central.StopScan();
            _svc.RaiseStatus("已发现房间，正在连接…");
            _peripheralDelegate = new PeripheralDelegate(_svc);
            peripheral.Delegate = _peripheralDelegate;
            _svc._peripheral = peripheral;
            central.ConnectPeripheral(peripheral, null);
        }

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
            => peripheral.DiscoverServices(new CBUUID[] { ServiceUuid });

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
            => _svc.RaiseStatus("连接失败，请重试");

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
        {
            _svc._guestReady = false;
            _svc.OnDisconnected();
        }
    }

    // ================================================================
    //  从机回调：CBPeripheralDelegate（服务发现 / 订阅 / 收消息）
    // ================================================================
    private class PeripheralDelegate : CBPeripheralDelegate
    {
        private readonly BluetoothService _svc;
        public PeripheralDelegate(BluetoothService svc) => _svc = svc;

        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            if (peripheral.Services is null) return;
            foreach (var svc in peripheral.Services)
            {
                if (svc.UUID?.Equals(ServiceUuid) == true)
                    peripheral.DiscoverCharacteristics(new CBUUID[] { CharUuid }, svc);
            }
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            if (service.Characteristics is null) return;
            foreach (var ch in service.Characteristics)
            {
                if (ch.UUID?.Equals(CharUuid) == true)
                {
                    _svc._guestChar = ch;
                    // 写 CCCD 开启 Notify → Android 主机收到 OnDescriptorWriteRequest
                    peripheral.SetNotifyValue(true, ch);
                }
            }
        }

        public override void UpdatedNotificationState(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (error is null && characteristic.IsNotifying)
            {
                _svc._guestReady = true;
                _svc.OnConnected();
            }
        }

        // 收到主机 Notify（握手 / 落子 / 重开消息）
        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
            => _svc.OnReceive(characteristic.Value);
    }
}
