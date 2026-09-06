#if !(ANDROID || IOS || WINDOWS)
namespace Gomoku.Services;

/// <summary>无蓝牙实现平台的桩实现。</summary>
public class UnsupportedBluetoothService : IBluetoothService
{
    public bool IsConnected => false;
    public BluetoothRole Role => BluetoothRole.None;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    public Task<bool> StartHostingAsync()
    {
        StatusChanged?.Invoke(this, "当前平台不支持蓝牙对战");
        return Task.FromResult(false);
    }

    public Task<bool> StartJoiningAsync()
    {
        StatusChanged?.Invoke(this, "当前平台不支持蓝牙对战");
        return Task.FromResult(false);
    }

    public Task SendAsync(string message) => Task.CompletedTask;

    public void Stop() { }
}
#endif
