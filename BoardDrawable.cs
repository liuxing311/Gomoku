using Microsoft.Maui.Graphics;

namespace Gomoku;

/// <summary>
/// 棋盘几何计算：根据画布尺寸算出边距、格距和原点。
/// 绘制与点击命中检测共用同一套计算，保证落子位置与画面一致。
/// </summary>
public static class BoardLayout
{
    public static (float OriginX, float OriginY, float Cell) Compute(float width, float height)
    {
        float boardSize = Math.Min(width, height);
        float margin = boardSize * 0.05f;
        float cell = (boardSize - margin * 2f) / (GameState.BoardSize - 1);
        float originX = (width - boardSize) / 2f + margin;
        float originY = (height - boardSize) / 2f + margin;
        return (originX, originY, cell);
    }
}

/// <summary>
/// 使用 MAUI Graphics 绘制木纹棋盘、网格线、星位、棋子和胜负标记。
/// </summary>
public class BoardDrawable : IDrawable
{
    private readonly GameState _game;

    public BoardDrawable(GameState game) => _game = game;

    /// <summary>落子前的预览光标位置（点击棋盘后显示，确认后落子）。</summary>
    public (int Row, int Col)? Cursor { get; set; }

    /// <summary>光标预览的棋子颜色：1 黑，2 白。</summary>
    public int CursorPlayer { get; set; } = 1;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width;
        float h = dirtyRect.Height;
        var (originX, originY, cell) = BoardLayout.Compute(w, h);

        // 木色背景
        canvas.FillColor = Color.FromArgb("#E3B566");
        canvas.FillRectangle(dirtyRect);

        int n = GameState.BoardSize;
        float gridLength = cell * (n - 1);
        Color lineColor = Color.FromArgb("#6B4A2B");

        // 网格线
        canvas.StrokeColor = lineColor;
        canvas.StrokeSize = Math.Max(1f, cell * 0.035f);
        for (int i = 0; i < n; i++)
        {
            float p = i * cell;
            canvas.DrawLine(originX, originY + p, originX + gridLength, originY + p); // 横线
            canvas.DrawLine(originX + p, originY, originX + p, originY + gridLength); // 竖线
        }

        // 外边框加粗
        canvas.StrokeSize = Math.Max(2f, cell * 0.08f);
        canvas.DrawRectangle(originX - cell * 0.18f, originY - cell * 0.18f,
            gridLength + cell * 0.36f, gridLength + cell * 0.36f);

        // 星位（15 路棋盘：四角星 + 天元）
        canvas.FillColor = lineColor;
        (int, int)[] stars = [(3, 3), (3, 11), (7, 7), (11, 3), (11, 11)];
        float starR = cell * 0.09f;
        foreach (var (sr, sc) in stars)
        {
            float cx = originX + sc * cell;
            float cy = originY + sr * cell;
            canvas.FillEllipse(cx - starR, cy - starR, starR * 2f, starR * 2f);
        }

        // 棋子
        float stoneR = cell * 0.46f;
        var winSet = _game.WinningLine?.ToHashSet() ?? new HashSet<(int, int)>();

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int v = _game.Board[r, c];
                if (v == 0) continue;

                float cx = originX + c * cell;
                float cy = originY + r * cell;
                bool isLast = _game.LastMove == (r, c);
                bool isWin = winSet.Contains((r, c));
                DrawStone(canvas, cx, cy, stoneR, v, isLast, isWin);
            }
        }

        // 落子预览光标（半透明棋子 + 虚线圆环）
        if (Cursor is { } cursor && !_game.IsGameOver
            && InBoard(cursor.Row, cursor.Col)
            && _game.Board[cursor.Row, cursor.Col] == 0)
        {
            float cx = originX + cursor.Col * cell;
            float cy = originY + cursor.Row * cell;
            DrawCursor(canvas, cx, cy, stoneR, CursorPlayer);
        }
    }

    private static bool InBoard(int r, int c) =>
        r >= 0 && r < GameState.BoardSize && c >= 0 && c < GameState.BoardSize;

    private static void DrawCursor(ICanvas canvas, float cx, float cy, float radius, int player)
    {
        // 半透明预览棋子
        canvas.FillColor = (player == 1 ? Color.FromArgb("#232323") : Color.FromArgb("#F8F5EC"))
            .WithAlpha(0.45f);
        canvas.FillEllipse(cx - radius, cy - radius, radius * 2f, radius * 2f);

        // 醒目的红色虚线圆环，提示"确认后落在此处"
        canvas.StrokeColor = Color.FromArgb("#E53935");
        canvas.StrokeSize = Math.Max(2f, radius * 0.1f);
        canvas.StrokeDashPattern = new[] { radius * 0.28f, radius * 0.22f };
        canvas.DrawEllipse(cx - radius * 1.12f, cy - radius * 1.12f,
            radius * 2.24f, radius * 2.24f);
        canvas.StrokeDashPattern = null;
    }

    private static void DrawStone(ICanvas canvas, float cx, float cy, float radius,
        int player, bool isLast, bool isWinning)
    {
        // 阴影
        canvas.FillColor = Colors.Black.WithAlpha(0.25f);
        canvas.FillEllipse(cx - radius + radius * 0.12f, cy - radius + radius * 0.16f,
            radius * 2f, radius * 2f);

        if (player == 1)
        {
            // 黑棋
            canvas.FillColor = Color.FromArgb("#232323");
            canvas.FillEllipse(cx - radius, cy - radius, radius * 2f, radius * 2f);

            // 高光
            canvas.FillColor = Colors.White.WithAlpha(0.22f);
            canvas.FillEllipse(cx - radius * 0.55f, cy - radius * 0.62f,
                radius * 0.75f, radius * 0.55f);
        }
        else
        {
            // 白棋
            canvas.FillColor = Color.FromArgb("#F8F5EC");
            canvas.FillEllipse(cx - radius, cy - radius, radius * 2f, radius * 2f);
            canvas.StrokeColor = Color.FromArgb("#B4AD9E");
            canvas.StrokeSize = Math.Max(1f, radius * 0.06f);
            canvas.DrawEllipse(cx - radius, cy - radius, radius * 2f, radius * 2f);

            // 高光
            canvas.FillColor = Colors.White.WithAlpha(0.9f);
            canvas.FillEllipse(cx - radius * 0.55f, cy - radius * 0.62f,
                radius * 0.6f, radius * 0.42f);
        }

        // 上一步落子标记（小红点）
        if (isLast && !isWinning)
        {
            float dotR = radius * 0.18f;
            canvas.FillColor = Color.FromArgb("#E53935");
            canvas.FillEllipse(cx - dotR, cy - dotR, dotR * 2f, dotR * 2f);
        }

        // 获胜连子标记（红色圆圈）
        if (isWinning)
        {
            canvas.StrokeColor = Color.FromArgb("#E53935");
            canvas.StrokeSize = Math.Max(2.5f, radius * 0.12f);
            canvas.DrawEllipse(cx - radius * 1.05f, cy - radius * 1.05f,
                radius * 2.1f, radius * 2.1f);
        }
    }
}
