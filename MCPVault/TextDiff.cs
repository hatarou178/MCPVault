using System.Text;

/// <summary>
/// 行単位の簡易 unified diff。write / edit / append / rollback の return=diff で使う。
/// </summary>
public static class TextDiff
{
    private const long MaxCells = 4_000_000;   // LCS 表の上限（行数の積）

    public static string Unified(string before, string after, int context = 2, int maxLines = 200)
    {
        if (before == after) return "(変更なし)";

        var a = before.Length == 0 ? [] : before.Split('\n');
        var b = after.Length  == 0 ? [] : after.Split('\n');

        if ((long)a.Length * b.Length > MaxCells)
            return $"(diff 省略: {a.Length} 行 → {b.Length} 行。サイズが大きいため差分は出力しません)";

        // LCS 表
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        // 操作列（' ' 共通 / '-' 削除 / '+' 追加）
        var ops = new List<(char Tag, string Line, int ALine, int BLine)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])             { ops.Add((' ', a[x], x, y)); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('-', a[x], x, y)); x++; }
            else                          { ops.Add(('+', b[y], x, y)); y++; }
        }
        while (x < a.Length) { ops.Add(('-', a[x], x, y)); x++; }
        while (y < b.Length) { ops.Add(('+', b[y], x, y)); y++; }

        // 変更行の前後 context 行だけをハンクとして出力
        var sb = new StringBuilder();
        int emitted = 0;
        int idx = 0;
        while (idx < ops.Count)
        {
            if (ops[idx].Tag == ' ') { idx++; continue; }

            int start = Math.Max(0, idx - context);
            int end   = idx;
            // 変更が連続する範囲（間の共通行が context*2 以下なら同じハンク）
            while (end < ops.Count)
            {
                if (ops[end].Tag != ' ') { end++; continue; }
                int run = 0;
                while (end + run < ops.Count && ops[end + run].Tag == ' ') run++;
                if (end + run >= ops.Count || run > context * 2) break;
                end += run;
            }
            int stop = Math.Min(ops.Count, end + context);

            int aStart = ops[start].ALine + 1, bStart = ops[start].BLine + 1;
            int aCount = ops.Skip(start).Take(stop - start).Count(o => o.Tag != '+');
            int bCount = ops.Skip(start).Take(stop - start).Count(o => o.Tag != '-');
            sb.AppendLine($"@@ -{aStart},{aCount} +{bStart},{bCount} @@");
            for (int k = start; k < stop; k++)
            {
                if (++emitted > maxLines)
                {
                    sb.AppendLine($"... (以降 {ops.Count - k} 行省略)");
                    return sb.ToString().TrimEnd();
                }
                sb.Append(ops[k].Tag).AppendLine(ops[k].Line);
            }
            idx = stop;
        }
        return sb.ToString().TrimEnd();
    }
}
