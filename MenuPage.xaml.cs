using Gomoku.Services;

namespace Gomoku;

public partial class MenuPage : ContentPage
{
    private readonly IBluetoothService _bluetooth;
    private bool _waiting;

    public MenuPage(IBluetoothService bluetooth)
    {
        InitializeComponent();
        _bluetooth = bluetooth;

#if !ANDROID
        // 蓝牙对战目前仅在 Android 上实现
        BtSection.IsVisible = false;
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _bluetooth.StatusChanged += OnBtStatusChanged;
        _bluetooth.Connected += OnBtConnected;
        _bluetooth.Disconnected += OnBtDisconnected;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _bluetooth.StatusChanged -= OnBtStatusChanged;
        _bluetooth.Connected -= OnBtConnected;
        _bluetooth.Disconnected -= OnBtDisconnected;
    }

    private void OnLocalClicked(object? sender, EventArgs e)
        => Shell.Current.GoToAsync(nameof(GamePage) + "?mode=local");

    private void OnAiPlayerFirstClicked(object? sender, EventArgs e)
        => Shell.Current.GoToAsync(nameof(GamePage) + "?mode=ai&first=player");

    private void OnAiAiFirstClicked(object? sender, EventArgs e)
        => Shell.Current.GoToAsync(nameof(GamePage) + "?mode=ai&first=ai");

    private async void OnHostClicked(object? sender, EventArgs e)
    {
        _waiting = true;
        CancelBtButton.IsVisible = true;
        BtStatusLabel.Text = "正在创建房间…";
        await _bluetooth.StartHostingAsync();
    }

    private async void OnGuestClicked(object? sender, EventArgs e)
    {
        _waiting = true;
        CancelBtButton.IsVisible = true;
        BtStatusLabel.Text = "正在搜索房间…";
        await _bluetooth.StartJoiningAsync();
    }

    private void OnCancelBtClicked(object? sender, EventArgs e)
    {
        _waiting = false;
        _bluetooth.Stop();
        CancelBtButton.IsVisible = false;
        BtStatusLabel.Text = "两台手机各装本应用：一台点创建房间（执黑先行），一台点加入房间（执白），靠近即自动连接";
    }

    private void OnBtStatusChanged(object? sender, string message)
    {
        BtStatusLabel.Text = message;
    }

    private async void OnBtConnected(object? sender, EventArgs e)
    {
        if (!_waiting) return;
        _waiting = false;
        CancelBtButton.IsVisible = false;

        string role = _bluetooth.Role == BluetoothRole.Host ? "host" : "guest";
        await Shell.Current.GoToAsync(nameof(GamePage) + $"?mode=bt&role={role}");
    }

    private void OnBtDisconnected(object? sender, EventArgs e)
    {
        if (_waiting)
        {
            _waiting = false;
            CancelBtButton.IsVisible = false;
            BtStatusLabel.Text = "连接断开，请重试";
        }
    }
}
