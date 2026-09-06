namespace Gomoku.Services;

/// <summary>蓝牙对战中的角色。</summary>
public enum BluetoothRole
{
    None,
    /// <summary>主机：创建房间、广播、执黑先行。</summary>
    Host,
    /// <summary>从机：扫描加入、执白。</summary>
    Guest
}

/// <summary>
/// 蓝牙对战传输层。消息协议（UTF-8，单条短报文）：
/// "S"       会话开始/握手
/// "M{r},{c}" 落子，例如 "M7,7"
/// "R"       重新开始
/// </summary>
public interface IBluetoothService
{
    bool IsConnected { get; }
    BluetoothRole Role { get; }

    /// <summary>连接建立（双方握手完成，可以开局）。</summary>
    event EventHandler? Connected;

    /// <summary>连接断开。</summary>
    event EventHandler? Disconnected;

    /// <summary>收到对方消息，参数为协议文本。</summary>
    event EventHandler<string>? MessageReceived;

    /// <summary>状态提示文本变化（供界面显示"正在搜索…"等）。</summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>创建房间（主机模式，开始广播等待加入）。成功请求返回 true。</summary>
    Task<bool> StartHostingAsync();

    /// <summary>加入房间（从机模式，扫描并自动连接主机）。</summary>
    Task<bool> StartJoiningAsync();

    /// <summary>发送一条协议消息给对方。</summary>
    Task SendAsync(string message);

    /// <summary>停止广播/扫描并断开连接。</summary>
    void Stop();
}
