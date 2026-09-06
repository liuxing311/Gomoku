using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Gomoku.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Gomoku.Platforms.Windows.Services;

/// <summary>
/// 基于 Windows BLE GATT API 的蓝牙对战传输，与 Android/iOS 端协议完全对齐：
/// 主机（Host）= GattServiceProvider（GATT Server + 广播），执黑先行；
/// 从机（Guest）= BluetoothLEAdvertisementWatcher（扫描 + GATT Client），执白。
/// 双向通道：主机→从机用 Notify，从机→主机用 WriteWithResponse。
/// 注意：主机模式要求本机蓝牙适配器支持外设（Peripheral）角色。
/// </summary>
public class BluetoothService : IBluetoothService
{
    // 与 Android/iOS 端完全一致的 GATT 标识
    private static readonly Guid ServiceUuid = new("7d6b1a01-5f3e-4c8a-9e2d-8a1f6c0b3e11");
    private static readonly Guid CharUuid = new("7d6b1a02-5f3e-4c8a-9e2d-8a1f6c0b3e11");
    private const int MaxScanRetries = 8;

    // 主机侧（GATT Server）
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _hostChar;
    private int _lastSubscriberCount;

    // 从机侧（GATT Client）
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _bleDevice;
    private GattCharacteristic? _guestChar;
    private bool _connecting;
    private bool _guestReady;
    private int _scanRetries;

    public bool IsConnected { get; private set; }
    public BluetoothRole Role { get; private set; } = BluetoothRole.None;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    // ---------------------------------------------------------------- 主机

    public async Task<bool> StartHostingAsync()
    {
        Stop();
        Role = BluetoothRole.Host;
        RaiseStatus("正在创建房间…");
        try
        {
            // 预检：本机蓝牙必须支持外设（Peripheral）角色才能当主机
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsPeripheralRoleSupported)
            {
                RaiseStatus("本机蓝牙不支持主机模式，请改用手机创建房间、电脑点加入房间");
                Role = BluetoothRole.None;
                return false;
            }

            var result = await GattServiceProvider.CreateAsync(ServiceUuid);
            _provider = result.ServiceProvider;
            _provider.AdvertisementStatusChanged += OnAdvStatusChanged;

            var param = new GattLocalCharacteristicParameters
            {
                // Notify（主机→从机）+ Write（从机→主机），与 Android/iOS 端一致
                CharacteristicProperties = GattCharacteristicProperties.Notify | GattCharacteristicProperties.Write
            };
            var chResult = await _provider.Service.CreateCharacteristicAsync(CharUuid, param);
            _hostChar = chResult.Characteristic;
            _hostChar.WriteRequested += OnHostWriteRequested;
            _hostChar.SubscribedClientsChanged += OnSubscribedClientsChanged;
            _lastSubscriberCount = 0;

            // 可连接+可发现广播：手机端按服务 UUID 过滤扫描才能发现；
            // 无参 StartAdvertising() 不含服务 UUID，手机永远扫不到
            var advParams = new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true
            };
            _provider.StartAdvertising(advParams);
            return true;
        }
        catch (Exception ex)
        {
            RaiseStatus($"创建房间失败：{ex.Message}");
            Role = BluetoothRole.None;
            return false;
        }
    }

    private void OnAdvStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        switch (args.Status)
        {
            case GattServiceProviderAdvertisementStatus.Started:
                RaiseStatus("房间已创建，等待对方加入…（两台设备请靠近）");
                break;
            case GattServiceProviderAdvertisementStatus.Aborted:
                RaiseStatus("广播启动失败：本机蓝牙可能不支持外设模式，请改用加入房间");
                break;
            case GattServiceProviderAdvertisementStatus.Stopped:
                RaiseStatus("广播已停止，请重试");
                break;
        }
    }

    // 从机订阅 Notify（写入 CCCD）→ 通道双向就绪，发送握手
    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        int count = sender.SubscribedClients.Count;
        if (count > _lastSubscriberCount && !IsConnected)
        {
            OnConnected();
            _ = SendAsync("S");
        }
        else if (count == 0 && _lastSubscriberCount > 0)
        {
            OnDisconnected();
        }
        _lastSubscriberCount = count;
    }

    private async void OnHostWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;
            var msg = Encoding.UTF8.GetString(request.Value.ToArray());
            Receive(msg);
            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                try { request.Respond(); } catch { /* 从机可能已断开 */ }
            }
        }
        catch { /* 忽略无效写入 */ }
    }

    // ---------------------------------------------------------------- 从机

    public Task<bool> StartJoiningAsync()
    {
        Stop();
        Role = BluetoothRole.Guest;
        _connecting = false;
        _guestReady = false;
        _scanRetries = 0;
        StartWatcher();
        return Task.FromResult(true);
    }

    private void StartWatcher()
    {
        RaiseStatus(_scanRetries == 0
            ? "正在搜索附近的房间，请将设备靠近…"
            : $"继续搜索房间…（{_scanRetries}/{MaxScanRetries}）");

        _watcher?.Stop();
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.AdvertisementFilter = new BluetoothLEAdvertisementFilter
        {
            Advertisement = new BluetoothLEAdvertisement { ServiceUuids = { ServiceUuid } }
        };
        _watcher.Received += OnWatcherReceived;
        _watcher.Start();
    }

    private async void OnWatcherReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_connecting || _bleDevice is not null) return;
        _connecting = true;
        sender.Stop();

        RaiseStatus("已发现房间，正在连接…");
        var address = args.BluetoothAddress;
        try
        {
            // Android 主机用公共地址，iOS 主机用随机地址，依次尝试
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (dev is null)
                dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Random);
            if (dev is null) { ConnectFailed(); return; }

            _bleDevice = dev;
            dev.ConnectionStatusChanged += OnConnectionStatusChanged;

            var svcResult = await dev.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                _svcMissing = true;
                ConnectFailed();
                return;
            }

            var chResult = await svcResult.Services[0].GetCharacteristicsForUuidAsync(CharUuid, BluetoothCacheMode.Uncached);
            if (chResult.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
            {
                _svcMissing = true;
                ConnectFailed();
                return;
            }

            _guestChar = chResult.Characteristics[0];
            _guestChar.ValueChanged += OnGuestValueChanged;

            // 写 CCCD 开启 Notify → Android/iOS 主机收到订阅事件
            var sub = await _guestChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (sub == GattCommunicationStatus.Success)
            {
                _guestReady = true;
                OnConnected();
            }
            else
            {
                _svcMissing = true;
                ConnectFailed();
            }
        }
        catch (Exception ex)
        {
            RaiseStatus($"连接异常：{ex.Message}");
            CleanupGuest();
            RescheduleScan();
        }
    }

    private bool _svcMissing;

    private void ConnectFailed()
    {
        RaiseStatus(_svcMissing ? "房间服务未就绪，正在重试…" : "连接未成功，正在重试…");
        _svcMissing = false;
        CleanupGuest();
        RescheduleScan();
    }

    private void RescheduleScan()
    {
        _scanRetries++;
        if (Role == BluetoothRole.Guest && _scanRetries <= MaxScanRetries)
        {
            Task.Delay(2000).ContinueWith(_ => { if (Role == BluetoothRole.Guest && !_guestReady) StartWatcher(); });
        }
        else if (Role == BluetoothRole.Guest)
        {
            Role = BluetoothRole.None;
            RaiseStatus("多次连接失败，请确认双方都已开启蓝牙并靠近后重试");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_guestReady)
            return; // 连接建立前的抖动
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _guestReady = false;
            OnDisconnected();
        }
    }

    // 收到主机 Notify（握手 / 落子 / 重开消息）
    private void OnGuestValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var msg = Encoding.UTF8.GetString(args.CharacteristicValue.ToArray());
        Receive(msg);
    }

    // ---------------------------------------------------------------- 收发

    public async Task SendAsync(string message)
    {
        if (!IsConnected) return;
        var buffer = Encoding.UTF8.GetBytes(message).AsBuffer();
        try
        {
            if (Role == BluetoothRole.Host && _hostChar is not null)
            {
                await _hostChar.NotifyValueAsync(buffer);
            }
            else if (Role == BluetoothRole.Guest && _guestChar is not null && _guestReady)
            {
                // WithResponse 对应主机端 GATT Server 的写请求确认
                await _guestChar.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse);
            }
        }
        catch { /* 对端断开时发送失败属正常 */ }
    }

    private void Receive(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
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
        if (!IsConnected) return;
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

        if (_hostChar is not null)
        {
            _hostChar.WriteRequested -= OnHostWriteRequested;
            _hostChar.SubscribedClientsChanged -= OnSubscribedClientsChanged;
            _hostChar = null;
        }
        if (_provider is not null)
        {
            try { _provider.StopAdvertising(); } catch { }
            _provider.AdvertisementStatusChanged -= OnAdvStatusChanged;
            _provider = null;
        }

        CleanupGuest();

        try { _watcher?.Stop(); } catch { }
        _watcher = null;
    }

    private void CleanupGuest()
    {
        _connecting = false;
        _guestReady = false;
        _guestChar = null;
        if (_bleDevice is not null)
        {
            try
            {
                _bleDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
                if (_guestChar is not null) _guestChar.ValueChanged -= OnGuestValueChanged;
                _bleDevice.Dispose();
            }
            catch { }
            _bleDevice = null;
        }
    }
}
