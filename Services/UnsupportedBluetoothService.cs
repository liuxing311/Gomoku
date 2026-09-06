#if !ANDROID && !IOS
namespace Gomoku.Services;

/// <summary>非 Android 平台的桩实现（蓝牙对战仅在 Android 手机上支持）。</summary>
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
        StatusChanged?.Invoke(this, "蓝牙对战仅支持 Android 手机");
        return Task.FromResult(false);
    }

    public Task<bool> StartJoiningAsync()
    {
        StatusChanged?.Invoke(this, "蓝牙对战仅支持 Android 手机");
        return Task.FromResult(false);
    }

    public Task SendAsync(string message) => Task.CompletedTask;

    public void Stop() { }
}
#endif
