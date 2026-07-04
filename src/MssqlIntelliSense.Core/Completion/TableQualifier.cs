using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

/// <summary>
/// Kết quả của việc qualify một tên bảng/view không có schema prefix.
/// </summary>
public sealed record QualifyResult(
    int IdentifierOffset,
    int IdentifierLength,
    string QualifiedText,
    string Schema,
    string Name
);

/// <summary>
/// Tự động thêm schema prefix cho tên bảng/view thiếu schema (e.g. "Users" → "[dbo].[Users]").
/// Hỗ trợ toàn bộ câu SQL - qualify tất cả tên bảng không có prefix.
/// </summary>
public static class TableQualifier
{
    /// <summary>
    /// Tìm và qualify một tên bảng/view tại vị trí caret.
    /// Trả về null nếu không tìm thấy tên bảng hoặc đã có schema prefix.
    /// </summary>
    public static QualifyResult? TryQualifyAt(string sql, int caretOffset, DatabaseMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(sql) || metadata == null)
            return null;

        // Parse token stream để tìm identifier tại/gần caret
        try
        {
            using var reader = new System.IO.StringReader(sql);
            var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true);
            var tokens = parser.GetTokenStream(reader, out _);
            if (tokens == null) return null;

            var active = tokens
                .Where(t => t.TokenType != Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.WhiteSpace &&
                            t.TokenType != Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.SingleLineComment &&
                            t.TokenType != Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.MultilineComment)
                .ToList();

            // Tìm token tại caret
            var target = active.FirstOrDefault(t =>
                t.Offset <= caretOffset && t.Offset + t.Text.Length >= caretOffset &&
                SqlCompletionHelper.IsIdentifierOrKeyword(t));

            if (target == null) return null;

            var idx = active.IndexOf(target);

            // Kiểm tra xem có dot trước (đã qualified) không
            if (idx > 0 && active[idx - 1].TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Dot)
                return null; // Already has qualifier

            // Kiểm tra xem có dot sau (là qualifier của thứ khác) không
            if (idx < active.Count - 1 && active[idx + 1].TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Dot)
                return null; // This IS a qualifier already

            var name = SqlCompletionHelper.Unquote(target.Text);

            // Tìm bảng/view match
            var table = metadata.Tables.FirstOrDefault(t =>
                t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (table != null)
            {
                var qualified = $"{SqlCompletionHelper.Quote(table.Schema)}.{SqlCompletionHelper.Quote(table.Name)}";
                return new QualifyResult(target.Offset, target.Text.Length, qualified, table.Schema, table.Name);
            }

            var view = metadata.Views.FirstOrDefault(v =>
                v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (view != null)
            {
                var qualified = $"{SqlCompletionHelper.Quote(view.Schema)}.{SqlCompletionHelper.Quote(view.Name)}";
                return new QualifyResult(target.Offset, target.Text.Length, qualified, view.Schema, view.Name);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Qualify tất cả tên bảng/view trong câu SQL chưa có schema prefix.
    /// Trả về SQL đã được qualify hoàn toàn.
    /// </summary>
    public static string QualifyAll(string sql, DatabaseMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(sql) || metadata == null)
            return sql;

        try
        {
            using var reader = new System.IO.StringReader(sql);
            var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true);
            var tokens = parser.GetTokenStream(reader, out _);
            if (tokens == null) return sql;

            var active = tokens
                .Where(t => t.TokenType != Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.WhiteSpace &&
                            t.TokenType != Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.SingleLineComment &&
                            t.TokenType != Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.MultilineComment)
                .ToList();

            // Thu thập các replacements từ cuối về đầu để không shift offset
            var replacements = new List<(int Offset, int Length, string Replacement)>();

            for (int i = 0; i < active.Count; i++)
            {
                var t = active[i];
                if (!SqlCompletionHelper.IsIdentifierOrKeyword(t)) continue;

                // Bỏ qua nếu đã có dot prefix (qualified)
                if (i > 0 && active[i - 1].TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Dot) continue;
                // Bỏ qua nếu là qualifier (có dot sau)
                if (i < active.Count - 1 && active[i + 1].TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Dot) continue;

                // Chỉ qualify nếu đứng sau FROM, JOIN, INTO, UPDATE, (không phải alias)
                if (i == 0) continue;
                var prev = active[i - 1];
                bool isTablePosition =
                    prev.TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.From ||
                    prev.TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Join ||
                    prev.TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Into ||
                    prev.TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Update;

                if (!isTablePosition) continue;

                var name = SqlCompletionHelper.Unquote(t.Text);

                var table = metadata.Tables.FirstOrDefault(tbl =>
                    tbl.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (table != null)
                {
                    replacements.Add((t.Offset, t.Text.Length,
                        $"{SqlCompletionHelper.Quote(table.Schema)}.{SqlCompletionHelper.Quote(table.Name)}"));
                    continue;
                }

                var view = metadata.Views.FirstOrDefault(v =>
                    v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (view != null)
                {
                    replacements.Add((t.Offset, t.Text.Length,
                        $"{SqlCompletionHelper.Quote(view.Schema)}.{SqlCompletionHelper.Quote(view.Name)}"));
                }
            }

            if (replacements.Count == 0) return sql;

            // Áp dụng replacements từ cuối về đầu
            var sb = new StringBuilder(sql);
            foreach (var (offset, length, replacement) in replacements.OrderByDescending(r => r.Offset))
            {
                sb.Remove(offset, length);
                sb.Insert(offset, replacement);
            }

            return sb.ToString();
        }
        catch
        {
            return sql;
        }
    }
}
