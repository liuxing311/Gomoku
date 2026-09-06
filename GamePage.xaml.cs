using Gomoku.Ai;
using Gomoku.Services;

namespace Gomoku;

[QueryProperty(nameof(Mode), "mode")]
[QueryProperty(nameof(First), "first")]
[QueryProperty(nameof(Role), "role")]
public partial class GamePage : ContentPage
{
    private readonly IBluetoothService _bt;
    private readonly GameState _game = new();
    private BoardDrawable? _drawable;
    private (int Row, int Col)? _cursor;
    private bool _aiPending;

    /// <summary>游戏模式：local 双人同屏 / ai 人机 / bt 蓝牙。</summary>
    public string Mode { get; set; } = "local";
    /// <summary>人机模式谁先手：player / ai。</summary>
    public string First { get; set; } = "player";
    /// <summary>蓝牙角色：host（执黑）/ guest（执白）。</summary>
    public string Role { get; set; } = "host";

    private int LocalColor => Role == "guest" ? 2 : 1;
    private int AiColor => First == "ai" ? 1 : 2;

    public GamePage(IBluetoothService bluetooth)
    {
        InitializeComponent();
        _bt = bluetooth;
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);

        _drawable = new BoardDrawable(_game);
        BoardView.Drawable = _drawable;

        var tap = new TapGestureRecognizer();
        tap.Tapped += OnBoardTapped;
        BoardView.GestureRecognizers.Add(tap);

        TitleLabel.Text = Mode switch
        {
            "ai" => "人机对战",
            "bt" => "蓝牙对战",
            _ => "双人同屏"
        };

        if (Mode == "bt")
        {
            _bt.MessageReceived += OnBtMessage;
            _bt.Disconnected += OnBtDisconnected;
        }

        RefreshUI();

        // AI 执黑时让 AI 先行
        if (Mode == "ai" && First == "ai")
            ScheduleAiMove();
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);

        if (Mode == "bt")
        {
            _bt.MessageReceived -= OnBtMessage;
            _bt.Disconnected -= OnBtDisconnected;
            _bt.Stop(); // 返回菜单即断开，对方会收到断开通知
        }
    }

    /// <summary>当前是否轮到本机玩家操作。</summary>
    private bool IsLocalTurn =>
        !_game.IsGameOver && Mode switch
        {
            "ai" => _game.CurrentPlayer != AiColor && !_aiPending,
            "bt" => _game.CurrentPlayer == LocalColor && _bt.IsConnected,
            _ => true
        };

    // ---------------------------------------------------------------- 棋盘交互

    private void OnBoardTapped(object? sender, TappedEventArgs e)
    {
        if (!IsLocalTurn) return;

        Point? position = e.GetPosition(BoardView);
        if (position is null || BoardView.Width <= 0 || BoardView.Height <= 0) return;

        var (originX, originY, cell) =
            BoardLayout.Compute((float)BoardView.Width, (float)BoardView.Height);

        float px = (float)position.Value.X - originX;
        float py = (float)position.Value.Y - originY;

        int col = (int)Math.Round(px / cell);
        int row = (int)Math.Round(py / cell);

        // 离交叉点太远则忽略
        float dx = px - col * cell;
        float dy = py - row * cell;
        if (Math.Sqrt(dx * dx + dy * dy) > cell * 0.5) return;

        if (row < 0 || row >= GameState.BoardSize || col < 0 || col >= GameState.BoardSize) return;
        if (_game.Board[row, col] != 0) return;

        // 第一次点击：显示光标；点别的交叉点：移动光标
        _cursor = (row, col);
        _drawable!.Cursor = _cursor;
        _drawable.CursorPlayer = _game.CurrentPlayer;
        BoardView.Invalidate();
        RefreshUI();
    }

    private void OnConfirmClicked(object? sender, EventArgs e)
    {
        if (_cursor is not { } c || !IsLocalTurn) return;

        if (!_game.TryPlace(c.Row, c.Col)) return;

        _cursor = null;
        _drawable!.Cursor = null;
        BoardView.Invalidate();

        // 落子后的联动：蓝牙同步给对方；人机模式触发 AI
        if (Mode == "bt")
            _ = _bt.SendAsync($"M{c.Row},{c.Col}");
        else if (Mode == "ai" && !_game.IsGameOver && _game.CurrentPlayer == AiColor)
            ScheduleAiMove();

        RefreshUI();
    }

    private void OnResetClicked(object? sender, EventArgs e)
    {
        _game.Reset();
        _cursor = null;
        if (_drawable is not null) _drawable.Cursor = null;
        BoardView.Invalidate();

        if (Mode == "bt")
            _ = _bt.SendAsync("R");

        RefreshUI();

        if (Mode == "ai" && First == "ai")
            ScheduleAiMove();
    }

    // ---------------------------------------------------------------- 人机 AI

    private void ScheduleAiMove()
    {
        if (_aiPending || _game.IsGameOver) return;
        _aiPending = true;
        RefreshUI();

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), () =>
        {
            _aiPending = false;
            if (Mode != "ai" || _game.IsGameOver || _game.CurrentPlayer != AiColor) return;

            var (r, c) = GomokuAi.FindBestMove(_game, AiColor);
            if (_game.TryPlace(r, c))
            {
                BoardView.Invalidate();
                RefreshUI();
            }
        });
    }

    // ---------------------------------------------------------------- 蓝牙消息

    private void OnBtMessage(object? sender, string msg)
    {
        switch (msg)
        {
            case "S":
                // 握手消息：进入页面时棋盘已是初始状态，无需处理
                break;

            case "R":
                _game.Reset();
                _cursor = null;
                if (_drawable is not null) _drawable.Cursor = null;
                BoardView.Invalidate();
                RefreshUI();
                break;

            default:
                if (msg.StartsWith("M"))
                {
                    var parts = msg[1..].Split(',');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out int r)
                        && int.TryParse(parts[1], out int c)
                        && r >= 0 && r < GameState.BoardSize
                        && c >= 0 && c < GameState.BoardSize)
                    {
                        _cursor = null;
                        if (_drawable is not null) _drawable.Cursor = null;
                        _game.TryPlace(r, c);
                        BoardView.Invalidate();
                        RefreshUI();
                    }
                }
                break;
        }
    }

    private void OnBtDisconnected(object? sender, EventArgs e) => RefreshUI();

    // ---------------------------------------------------------------- 界面状态

    private void RefreshUI()
    {
        if (_drawable is not null)
        {
            _drawable.Cursor = _cursor;
            _drawable.CursorPlayer = _game.CurrentPlayer;
        }

        ConfirmButton.IsEnabled = _cursor is not null && IsLocalTurn;

        // 蓝牙中断
        if (Mode == "bt" && !_bt.IsConnected && !_game.IsGameOver)
        {
            StatusLabel.Text = "蓝牙连接已断开";
            StatusLabel.TextColor = Color.FromArgb("#C62828");
            return;
        }

        if (_game.IsGameOver)
        {
            StatusLabel.Text = EndGameText();
            StatusLabel.TextColor = Color.FromArgb("#C62828");
            return;
        }

        if (Mode == "ai")
        {
            StatusLabel.Text = _game.CurrentPlayer == AiColor
                ? "AI 思考中…"
                : $"轮到你了（{StoneName(_game.CurrentPlayer)}），点棋盘选点";
        }
        else if (Mode == "bt")
        {
            StatusLabel.Text = _game.CurrentPlayer == LocalColor
                ? $"轮到你了（{StoneName(LocalColor)}），点棋盘选点"
                : "等待对方落子…";
        }
        else
        {
            StatusLabel.Text = _game.CurrentPlayer == 1 ? "● 黑棋回合" : "○ 白棋回合";
        }

        StatusLabel.TextColor = Color.FromArgb("#5D4037");
        SemanticScreenReader.Announce(StatusLabel.Text);
    }

    private string EndGameText()
    {
        if (_game.WinningLine is null) return "棋盘已满，平局！";

        int winner = _game.CurrentPlayer; // 获胜方保留在 CurrentPlayer
        if (Mode == "ai")
            return winner == AiColor ? "AI 获胜！" : "恭喜，你赢了！";
        if (Mode == "bt")
            return winner == LocalColor ? "你赢了！" : "对方获胜！";
        return winner == 1 ? "● 黑棋胜利！" : "○ 白棋胜利！";
    }

    private static string StoneName(int color) => color == 1 ? "● 黑棋" : "○ 白棋";
}
