namespace Gomoku;

/// <summary>
/// 五子棋游戏状态与规则逻辑（15x15 标准棋盘，黑先）。
/// 棋盘取值：0 = 空，1 = 黑棋，2 = 白棋。
/// </summary>
public class GameState
{
    public const int BoardSize = 15;

    /// <summary>棋盘交叉点状态：[row, col]，0 空 / 1 黑 / 2 白。</summary>
    public int[,] Board { get; } = new int[BoardSize, BoardSize];

    /// <summary>当前轮到哪方：1 黑棋，2 白棋。</summary>
    public int CurrentPlayer { get; private set; } = 1;

    /// <summary>游戏是否结束（有人获胜或平局）。</summary>
    public bool IsGameOver { get; private set; }

    /// <summary>上一步落子位置，用于标记。</summary>
    public (int Row, int Col)? LastMove { get; private set; }

    /// <summary>获胜的五颗连子位置（平局或未结束为 null）。</summary>
    public List<(int Row, int Col)>? WinningLine { get; private set; }

    /// <summary>已落子总数，用于平局判定。</summary>
    public int MoveCount { get; private set; }

    public void Reset()
    {
        Array.Clear(Board);
        CurrentPlayer = 1;
        IsGameOver = false;
        LastMove = null;
        WinningLine = null;
        MoveCount = 0;
    }

    /// <summary>
    /// 尝试在 (row, col) 落子。成功返回 true；若位置非法、已有棋子或游戏已结束则返回 false。
    /// </summary>
    public bool TryPlace(int row, int col)
    {
        if (IsGameOver) return false;
        if (row < 0 || row >= BoardSize || col < 0 || col >= BoardSize) return false;
        if (Board[row, col] != 0) return false;

        int player = CurrentPlayer;
        Board[row, col] = player;
        LastMove = (row, col);
        MoveCount++;

        WinningLine = FindWinningLine(row, col, player);
        if (WinningLine is not null)
        {
            IsGameOver = true; // CurrentPlayer 保留为获胜方
        }
        else if (MoveCount >= BoardSize * BoardSize)
        {
            IsGameOver = true; // 棋盘下满，平局
        }
        else
        {
            CurrentPlayer = player == 1 ? 2 : 1;
        }

        return true;
    }

    /// <summary>
    /// 检查以 (row, col) 为中心的横、竖、两条斜线是否形成五连。
    /// 返回连成一线的所有棋子位置（>=5），未形成返回 null。
    /// </summary>
    private List<(int Row, int Col)>? FindWinningLine(int row, int col, int player)
    {
        // 四个方向：横、竖、主对角线、副对角线
        (int Dr, int Dc)[] directions = [(0, 1), (1, 0), (1, 1), (1, -1)];

        foreach (var (dr, dc) in directions)
        {
            var line = new List<(int, int)> { (row, col) };

            // 正方向延伸
            for (int i = 1; ; i++)
            {
                int r = row + dr * i, c = col + dc * i;
                if (!InBoard(r, c) || Board[r, c] != player) break;
                line.Add((r, c));
            }

            // 反方向延伸
            for (int i = 1; ; i++)
            {
                int r = row - dr * i, c = col - dc * i;
                if (!InBoard(r, c) || Board[r, c] != player) break;
                line.Add((r, c));
            }

            if (line.Count >= 5)
            {
                line.Sort((a, b) => a.Item1 == b.Item1 ? a.Item2.CompareTo(b.Item2) : a.Item1.CompareTo(b.Item1));
                return line;
            }
        }

        return null;
    }

    private static bool InBoard(int r, int c) =>
        r >= 0 && r < BoardSize && c >= 0 && c < BoardSize;
}
