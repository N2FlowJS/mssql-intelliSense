using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

/// <summary>
/// Kết quả mở rộng SELECT * thành danh sách cột.
/// </summary>
public sealed record ExpandStarResult(
    /// <summary>Vị trí bắt đầu của dấu * trong SQL gốc (byte offset).</summary>
    int StarOffset,
    /// <summary>Độ dài chuỗi cần thay thế (luôn = 1 cho dấu *).</summary>
    int StarLength,
    /// <summary>Danh sách cột đã expand, sẵn sàng để chèn vào editor.</summary>
    string ExpandedText,
    /// <summary>Nguồn dữ liệu tìm thấy (bảng/view).</summary>
    IReadOnlyList<VisibleSource> Sources
);

/// <summary>
/// Mở rộng SELECT * thành danh sách cột cụ thể, hỗ trợ multi-table với alias.
/// </summary>
public static class SelectStarExpander
{
    /// <summary>
    /// Phân tích câu SQL và trả về thông tin cần thiết để expand SELECT *.
    /// Nếu không tìm thấy * hoặc không có bảng nào, trả về null.
    /// </summary>
    public static ExpandStarResult? TryExpand(string sql, int caretOffset, DatabaseMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(sql) || metadata == null)
            return null;

        // 1. Tìm vị trí dấu * tại hoặc gần caret
        int starOffset = FindStarOffset(sql, caretOffset);
        if (starOffset < 0) return null;

        // 2. Tìm sources (bảng/view) trong câu SQL hiện tại
        var sources = SqlContextAnalyzer.FindSources(sql, metadata);
        if (sources.Count == 0) return null;

        // 3. Xây dựng danh sách cột
        string expandedText = BuildColumnList(sources, indentStyle: DetectIndentStyle(sql, starOffset));

        return new ExpandStarResult(starOffset, 1, expandedText, sources);
    }

    /// <summary>
    /// Thực hiện expand trực tiếp trên chuỗi SQL - trả về SQL mới sau khi thay *.
    /// </summary>
    public static string? ExpandInSql(string sql, int caretOffset, DatabaseMetadata metadata)
    {
        var result = TryExpand(sql, caretOffset, metadata);
        if (result == null) return null;

        return sql.Substring(0, result.StarOffset)
            + result.ExpandedText
            + sql.Substring(result.StarOffset + result.StarLength);
    }

    // ─── Private helpers ───────────────────────────────────────────────────────

    private static int FindStarOffset(string sql, int caretOffset)
    {
        // Tìm dấu * gần caret nhất, nhưng nằm sau SELECT
        // Ưu tiên: * tại caret, sau đó scan trái/phải trong cùng một câu SELECT

        // Tìm * ở vị trí caret trước
        for (int i = Math.Min(caretOffset, sql.Length - 1); i >= 0; i--)
        {
            if (sql[i] == '*')
            {
                // Đảm bảo không phải operator (**) hay comment
                if (IsSelectStar(sql, i))
                    return i;
                break;
            }

            // Dừng nếu gặp end of statement or newline context switch
            if (sql[i] == ';') break;
        }

        // Tìm * sau caret
        for (int i = caretOffset; i < sql.Length; i++)
        {
            if (sql[i] == '*')
            {
                if (IsSelectStar(sql, i))
                    return i;
                break;
            }
            if (sql[i] == ';') break;
        }

        return -1;
    }

    private static bool IsSelectStar(string sql, int starIndex)
    {
        // Kiểm tra không phải *= hay */ hay * trong comment
        if (starIndex > 0 && sql[starIndex - 1] == '/') return false;  // /* comment
        if (starIndex < sql.Length - 1 && sql[starIndex + 1] == '/') return false;  // */ end comment
        if (starIndex < sql.Length - 1 && sql[starIndex + 1] == '=') return false;  // *=

        // Kiểm tra có keyword SELECT ở trước (trong cùng statement)
        var before = sql[..starIndex];
        var lastSemicolon = before.LastIndexOf(';');
        var segment = lastSemicolon >= 0 ? before[(lastSemicolon + 1)..] : before;
        return segment.Contains("SELECT", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("select", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildColumnList(IReadOnlyList<VisibleSource> sources, IndentStyle indentStyle)
    {
        // Nếu chỉ một source, không cần alias prefix
        if (sources.Count == 1)
        {
            var source = sources[0];
            if (source.Columns.Count == 0)
                return $"[{source.Alias}].*";

            var cols = source.Columns
                .OrderBy(c => c.Ordinal)
                .Select(c => SqlCompletionHelper.Quote(c.Name));

            return FormatColumns(cols, indentStyle);
        }

        // Nhiều source: prefix với alias
        var allCols = new List<string>();
        foreach (var source in sources)
        {
            var alias = SqlCompletionHelper.Quote(source.Alias);
            if (source.Columns.Count == 0)
            {
                allCols.Add($"{alias}.*");
                continue;
            }

            foreach (var col in source.Columns.OrderBy(c => c.Ordinal))
            {
                allCols.Add($"{alias}.{SqlCompletionHelper.Quote(col.Name)}");
            }
        }

        return FormatColumns(allCols, indentStyle);
    }

    private static string FormatColumns(IEnumerable<string> columns, IndentStyle style)
    {
        var cols = columns.ToList();
        if (cols.Count == 0) return "*";

        if (style == IndentStyle.Inline || cols.Count <= 3)
            return string.Join(", ", cols);

        // Multi-line: indent cho dễ đọc
        var indent = style == IndentStyle.Tab ? "\t" : "    ";
        var sb = new StringBuilder();
        sb.AppendLine();
        for (int i = 0; i < cols.Count; i++)
        {
            sb.Append(indent);
            sb.Append(cols[i]);
            if (i < cols.Count - 1) sb.AppendLine(",");
        }
        return sb.ToString();
    }

    private enum IndentStyle { Inline, Spaces, Tab }

    private static IndentStyle DetectIndentStyle(string sql, int starOffset)
    {
        // Phát hiện style indentation từ SQL hiện tại
        if (sql.Contains('\t')) return IndentStyle.Tab;

        // Kiểm tra độ dài dòng trước dấu *
        var lineStart = sql.LastIndexOf('\n', starOffset) + 1;
        var lineBeforeStar = sql[lineStart..starOffset].TrimStart();
        if (lineBeforeStar.Length < 20) return IndentStyle.Inline; // Short SELECT, inline

        return IndentStyle.Spaces;
    }
}
