namespace Gomoku.Ai;

/// <summary>
/// 五子棋人机 AI：对每个空位计算"自己落子的进攻分"与"对手落此点的威胁分"，
/// 按棋型（连五、活四、冲四、活三、眠三、活二…）加权求和，选最高分落子。
/// 计算量小，手机上瞬时完成。
/// </summary>
public static class GomokuAi
{
    private const int BoardSize = GameState.BoardSize;

    // 四个方向：横、竖、主对角线、副对角线
    private static readonly (int Dr, int Dc)[] Dirs = [(0, 1), (1, 0), (1, 1), (1, -1)];

    /// <summary>
    /// 为 aiPlayer 计算最佳落子点。棋盘为空时返回天元。
    /// </summary>
    public static (int Row, int Col) FindBestMove(GameState game, int aiPlayer)
    {
        var board = game.Board;

        if (game.MoveCount == 0)
            return (BoardSize / 2, BoardSize / 2);

        int opponent = aiPlayer == 1 ? 2 : 1;

        int bestR = -1, bestC = -1;
        double bestScore = double.NegativeInfinity;

        for (int r = 0; r < BoardSize; r++)
        {
            for (int c = 0; c < BoardSize; c++)
            {
                if (board[r, c] != 0) continue;
                if (!HasNeighbor(board, r, c, 2)) continue;

                double attack = EvaluatePoint(board, r, c, aiPlayer);
                double defense = EvaluatePoint(board, r, c, opponent);

                // 进攻略优先；微小的中心偏好避免开局乱跑；确定性抖动让同分选择不呆板
                double score = attack * 1.05 + defense;
                score += (7 - Math.Abs(r - 7) - Math.Abs(c - 7)) * 0.5;
                score += ((r * 31 + c * 17) % 13) * 0.01;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestR = r;
                    bestC = c;
                }
            }
        }

        // 兜底：随便找个空位
        if (bestR < 0)
        {
            for (int r = 0; r < BoardSize; r++)
                for (int c = 0; c < BoardSize; c++)
                    if (board[r, c] == 0) return (r, c);
        }

        return (bestR, bestC);
    }

    /// <summary>
    /// 评估 player 在 (row,col) 落一子的价值：四个方向的棋型分数之和。
    /// </summary>
    private static double EvaluatePoint(int[,] board, int row, int col, int player)
    {
        double total = 0;
        foreach (var (dr, dc) in Dirs)
        {
            int count = 1;   // 含假设落下的这一子
            int open = 0;    // 两端空位数

            // 正方向
            int r = row + dr, c = col + dc;
            while (InBoard(r, c) && board[r, c] == player) { count++; r += dr; c += dc; }
            if (InBoard(r, c) && board[r, c] == 0) open++;

            // 反方向
            r = row - dr; c = col - dc;
            while (InBoard(r, c) && board[r, c] == player) { count++; r -= dr; c -= dc; }
            if (InBoard(r, c) && board[r, c] == 0) open++;

            total += PatternScore(count, open);
        }
        return total;
    }

    /// <summary>棋型计分：连数 + 开放端数 → 分数。</summary>
    private static double PatternScore(int count, int open)
    {
        if (count >= 5) return 10_000_000;          // 连五，必胜
        if (open == 0) return 0;                    // 两端堵死，无威胁

        return count switch
        {
            4 => open == 2 ? 1_000_000 : 50_000,    // 活四 / 冲四
            3 => open == 2 ? 20_000 : 1_000,        // 活三 / 眠三
            2 => open == 2 ? 500 : 50,              // 活二 / 眠二
            _ => open == 2 ? 20 : 5                 // 活一 / 眠一
        };
    }

    /// <summary>附近 distance 范围内是否有棋子（过滤无关空位，减少计算）。</summary>
    private static bool HasNeighbor(int[,] board, int row, int col, int distance)
    {
        for (int dr = -distance; dr <= distance; dr++)
        {
            for (int dc = -distance; dc <= distance; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int r = row + dr, c = col + dc;
                if (InBoard(r, c) && board[r, c] != 0) return true;
            }
        }
        return false;
    }

    private static bool InBoard(int r, int c) =>
        r >= 0 && r < BoardSize && c >= 0 && c < BoardSize;
}
