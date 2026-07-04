using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Completion.Snippets;

public static class SnippetDefaults
{
    public static IReadOnlyList<Snippet> GetDefaultSnippets() =>
    [
        new Snippet
        {
            Prefix = "cte",
            Description = "WITH cte fragment",
            Body = "\nWITH $cte_name$ AS\n(\n$SELECTEDTEXT$\n)\nSELECT $CURSOR$\nFROM $cte_name$",
            Placeholders = [new SnippetPlaceholder("cte_name", "cte_name")]
        },
        new Snippet
        {
            Prefix = "sf",
            Description = "SELECT FROM",
            Body = "SELECT $CURSOR$\nFROM ",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "scf",
            Description = "SELECT COUNT(*) FROM",
            Body = "SELECT COUNT(*) FROM $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "s",
            Description = "SELECT",
            Body = "SELECT $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "f",
            Description = "FROM",
            Body = "\nFROM $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "w",
            Description = "WHERE",
            Body = "\nWHERE $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "o",
            Description = "ORDER BY",
            Body = "\nORDER BY $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "g",
            Description = "GROUP BY",
            Body = "\nGROUP BY $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "h",
            Description = "HAVING",
            Body = "\nHAVING $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "j",
            Description = "INNER JOIN",
            Body = "\nINNER JOIN $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "lj",
            Description = "LEFT JOIN",
            Body = "\nLEFT JOIN $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "rj",
            Description = "RIGHT JOIN",
            Body = "\nRIGHT JOIN $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "i",
            Description = "INSERT INTO",
            Body = "INSERT INTO $table$ ($columns$)\nVALUES ($CURSOR$)",
            Placeholders = [new SnippetPlaceholder("table", ""), new SnippetPlaceholder("columns", "")]
        },
        new Snippet
        {
            Prefix = "u",
            Description = "UPDATE",
            Body = "UPDATE $table$\nSET $CURSOR$",
            Placeholders = [new SnippetPlaceholder("table", "")]
        },
        new Snippet
        {
            Prefix = "d",
            Description = "DELETE FROM",
            Body = "DELETE FROM $table$\nWHERE $CURSOR$",
            Placeholders = [new SnippetPlaceholder("table", "")]
        },
        new Snippet
        {
            Prefix = "sel",
            Description = "SELECT TOP",
            Body = "SELECT TOP $CURSOR$ ",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "c",
            Description = "COUNT",
            Body = "COUNT($CURSOR$)",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "max",
            Description = "MAX",
            Body = "MAX($CURSOR$)",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "min",
            Description = "MIN",
            Body = "MIN($CURSOR$)",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "avg",
            Description = "AVG",
            Body = "AVG($CURSOR$)",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "sum",
            Description = "SUM",
            Body = "SUM($CURSOR$)",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "cv",
            Description = "CREATE VIEW",
            Body = "CREATE VIEW $view_name$\nAS\n$CURSOR$",
            Placeholders = [new SnippetPlaceholder("view_name", "")]
        },
        new Snippet
        {
            Prefix = "cp",
            Description = "CREATE PROCEDURE",
            Body = "CREATE PROCEDURE $proc_name$\n    $params$\nAS\nBEGIN\n    SET NOCOUNT ON;\n    $CURSOR$\nEND",
            Placeholders = [new SnippetPlaceholder("proc_name", ""), new SnippetPlaceholder("params", "")]
        },
        new Snippet
        {
            Prefix = "cf",
            Description = "CREATE FUNCTION",
            Body = "CREATE FUNCTION $func_name$ ($params$)\nRETURNS $return_type$\nAS\nBEGIN\n    $CURSOR$\nEND",
            Placeholders = [new SnippetPlaceholder("func_name", ""), new SnippetPlaceholder("params", ""), new SnippetPlaceholder("return_type", "int")]
        },
        new Snippet
        {
            Prefix = "t",
            Description = "CREATE TABLE",
            Body = "CREATE TABLE $table$ (\n    $CURSOR$\n)",
            Placeholders = [new SnippetPlaceholder("table", "")]
        },
        new Snippet
        {
            Prefix = "if",
            Description = "IF",
            Body = "IF $condition$\nBEGIN\n    $CURSOR$\nEND",
            Placeholders = [new SnippetPlaceholder("condition", "")]
        },
        new Snippet
        {
            Prefix = "ie",
            Description = "IF EXISTS",
            Body = "IF EXISTS ($CURSOR$)\nBEGIN\n\nEND",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "wh",
            Description = "WHILE",
            Body = "WHILE $condition$\nBEGIN\n    $CURSOR$\nEND",
            Placeholders = [new SnippetPlaceholder("condition", "")]
        },
        new Snippet
        {
            Prefix = "tr",
            Description = "BEGIN TRAN",
            Body = "BEGIN TRANSACTION;\n\n$CURSOR$\n\nCOMMIT TRANSACTION;",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "try",
            Description = "TRY CATCH",
            Body = "BEGIN TRY\n    $CURSOR$\nEND TRY\nBEGIN CATCH\n    THROW;\nEND CATCH",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "dt",
            Description = "DROP TABLE",
            Body = "DROP TABLE IF EXISTS $CURSOR$",
            Placeholders = []
        },
        new Snippet
        {
            Prefix = "ai",
            Description = "ALTER INDEX",
            Body = "ALTER INDEX $index_name$ ON $table$\nREBUILD;",
            Placeholders = [new SnippetPlaceholder("index_name", ""), new SnippetPlaceholder("table", "")]
        }
    ];
}
