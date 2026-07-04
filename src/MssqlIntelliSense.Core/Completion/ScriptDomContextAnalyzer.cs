using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

/// <summary>
/// AST-enhanced context analysis using ScriptDom's TSql160Parser.
/// Uses token-based analysis as the primary method (resilient to incomplete SQL),
/// then refines with AST-based context detection when the parse succeeds.
/// Now supports CTEs, subqueries, and nested context tracking.
/// </summary>
public static class ScriptDomContextAnalyzer
{
    public static void Analyze(
        string sql,
        int caretPosition,
        out IReadOnlyList<string> qualifiers,
        out string prefix,
        out bool isTableContext,
        out bool isProcedureContext,
        out bool isJoinContext,
        out bool isTypeContext,
        out string? targetTableSchema,
        out string? targetTableName,
        out bool isOrderByContext,
        out bool isComparisonRhsContext,
        out bool isOutputContext,
        out bool isInsertIntoContext,
        out bool isUpdateSetContext,
        out bool isFromOrJoinContext)
    {
        // Step 1: Token-based analysis (reliable for incomplete SQL)
        SqlContextAnalyzer.AnalyzeCaretContext(
            sql, caretPosition,
            out qualifiers, out prefix,
            out isTableContext, out isProcedureContext, out isJoinContext, out isTypeContext,
            out targetTableSchema, out targetTableName,
            out isOrderByContext, out isComparisonRhsContext,
            out isOutputContext, out isInsertIntoContext, out isUpdateSetContext, out isFromOrJoinContext);

        // Step 2: AST-based refinement
        using var reader = new StringReader(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment? fragment;
        try
        {
            fragment = parser.Parse(reader, out _);
        }
        catch
        {
            return;
        }

        if (fragment == null) return;

        var visitor = new CaretContextVisitor(caretPosition);
        fragment.Accept(visitor);

        if (!visitor.HasEvidence) return;

        // Only override flags when AST provides positive evidence
        if (visitor.InFromClause) isTableContext = true;
        if (visitor.InJoin) { isTableContext = true; isJoinContext = true; }
        if (visitor.InExecute) isProcedureContext = true;
        if (visitor.InInsertTarget) { isTableContext = true; isInsertIntoContext = true; }
        if (visitor.InUpdateTarget) { isTableContext = true; }
        if (visitor.InUpdateSet) isUpdateSetContext = true;
        if (visitor.InDeleteTarget) isTableContext = true;
        if (visitor.InMergeTarget) { isTableContext = true; isFromOrJoinContext = true; }
        if (visitor.InAlterDropTable) isTableContext = true;
        if (visitor.InDataType || visitor.InDeclareVariable) isTypeContext = true;
        if (visitor.InOrderBy) isOrderByContext = true;
        if (visitor.InGroupBy) isOrderByContext = true;
        if (visitor.InWhereClause && !visitor.InFromClause && !visitor.InCteBody)
            isComparisonRhsContext = true;
        if (visitor.InOutputInto) isOutputContext = true;
        if (visitor.InFromClause || visitor.InJoin) isFromOrJoinContext = true;

        // CTE body is a SELECT context — show tables, columns, keywords
        if (visitor.InCteBody && !visitor.InFromClause && !visitor.InJoin && !visitor.IsSubquery)
        {
            isTableContext = true;
        }
    }

    // ── AST context kinds for nesting ─────────────────────────────────────

    private enum AstContext
    {
        FromClause,
        Join,
        Execute,
        Insert,
        Update,
        SetClause,
        Delete,
        Merge,
        AlterDropTable,
        DataType,
        DeclareVariable,
        OrderBy,
        GroupBy,
        Where,
        OutputInto,
        CteBody,
        QuerySpec,
    }

    // ── Visitor with nesting stack ────────────────────────────────────────

    private sealed class CaretContextVisitor : TSqlFragmentVisitor
    {
        private readonly int _caret;
        private readonly Stack<AstContext> _contextStack = new();

        public CaretContextVisitor(int caret) => _caret = caret;

        public bool HasEvidence => InFromClause || InJoin || InExecute || InInsertTarget
            || InUpdateTarget || InUpdateSet || InDeleteTarget || InMergeTarget
            || InAlterDropTable || InDataType || InDeclareVariable || InOrderBy
            || InGroupBy || InWhereClause || InOutputInto || InCteBody;

        public bool InFromClause => _contextStack.Contains(AstContext.FromClause);
        public bool InJoin => _contextStack.Contains(AstContext.Join);
        public bool InExecute => _contextStack.Contains(AstContext.Execute);
        public bool InInsertTarget => _contextStack.Contains(AstContext.Insert);
        public bool InUpdateTarget => _contextStack.Contains(AstContext.Update);
        public bool InUpdateSet => _contextStack.Contains(AstContext.SetClause);
        public bool InDeleteTarget => _contextStack.Contains(AstContext.Delete);
        public bool InMergeTarget => _contextStack.Contains(AstContext.Merge);
        public bool InAlterDropTable => _contextStack.Contains(AstContext.AlterDropTable);
        public bool InDataType => _contextStack.Contains(AstContext.DataType);
        public bool InDeclareVariable => _contextStack.Contains(AstContext.DeclareVariable);
        public bool InOrderBy => _contextStack.Contains(AstContext.OrderBy);
        public bool InGroupBy => _contextStack.Contains(AstContext.GroupBy);
        public bool InWhereClause => _contextStack.Contains(AstContext.Where);
        public bool InOutputInto => _contextStack.Contains(AstContext.OutputInto);
        public bool InCteBody => _contextStack.Contains(AstContext.CteBody);
        /// <summary>True when the caret is inside a subquery (nested QuerySpecification).</summary>
        public bool IsSubquery => _contextStack.Count(c => c == AstContext.QuerySpec) > 1;

        // ── CTE ───────────────────────────────────────────────────────────

        public override void ExplicitVisit(WithCtesAndXmlNamespaces node)
        {
            if (!ContainsCaret(node)) return;
            foreach (var cte in node.CommonTableExpressions)
                cte.Accept(this);
        }

        public override void ExplicitVisit(CommonTableExpression node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.CteBody);
            node.QueryExpression?.Accept(this);
            _contextStack.Pop();
        }

        // ── Query — tracking subquery nesting ─────────────────────────────

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.QuerySpec);
            node.FromClause?.Accept(this);
            node.WhereClause?.Accept(this);
            node.GroupByClause?.Accept(this);
            node.OrderByClause?.Accept(this);
            _contextStack.Pop();
        }

        // ── Existing contexts (converted to stack-based) ──────────────────

        public override void ExplicitVisit(FromClause node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.FromClause);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Join);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(UnqualifiedJoin node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Join);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(ExecuteSpecification node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Execute);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Insert);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Update);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(SetClause node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.SetClause);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Delete);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Merge);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(AlterTableStatement node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.AlterDropTable);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(DropTableStatement node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.AlterDropTable);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.AlterDropTable);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.DeclareVariable);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(DataTypeReference node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.DataType);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(OrderByClause node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.OrderBy);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(GroupByClause node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.GroupBy);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(WhereClause node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.Where);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        public override void ExplicitVisit(OutputIntoClause node)
        {
            if (!ContainsCaret(node)) return;
            _contextStack.Push(AstContext.OutputInto);
            base.ExplicitVisit(node);
            _contextStack.Pop();
        }

        private bool ContainsCaret(TSqlFragment node) =>
            node.StartOffset < _caret && _caret < node.StartOffset + node.FragmentLength;
    }
}
