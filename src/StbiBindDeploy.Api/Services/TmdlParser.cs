using System.Text.RegularExpressions;
using StbiBindDeploy.Api.Models;

namespace StbiBindDeploy.Api.Services;

public interface ITmdlParser
{
    /// <summary>
    /// Parses a set of TMDL file contents (path -> raw text, same shape as S7's
    /// TmdlAuthoringResult.Files) into a structured model this service can push to the Power BI
    /// Push Dataset API. Deliberately simple and line/block-based rather than a full TMDL
    /// grammar parser — TMDL's actual structure is "a keyword line starts a block, subsequent
    /// more-indented lines are its properties until the next keyword line", which this matches
    /// directly rather than tracking exact tab/space indentation depth (robust to an LLM author
    /// using spaces instead of tabs, or inconsistent indentation width).
    /// </summary>
    ParsedSemanticModel Parse(IReadOnlyDictionary<string, string> filesByPath);
}

public sealed class TmdlParser : ITmdlParser
{
    private static readonly Regex TableDeclaration = new(@"^\s*table\s+(\S+)\s*$", RegexOptions.Multiline);
    private static readonly Regex BlockStart = new(@"^\s*(column|measure|partition|annotation)\b", RegexOptions.Multiline);
    private static readonly Regex ColumnStart = new(@"^\s*column\s+'?([^'\r\n]+?)'?\s*$");
    private static readonly Regex MeasureStart = new(@"^\s*measure\s+'([^']+)'\s*=\s*(.+)$");
    private static readonly Regex RelationshipStart = new(@"^\s*relationship\s+\S+\s*$", RegexOptions.Multiline);
    private static readonly Regex FromColumn = new(@"^\s*fromColumn:\s*([^.\r\n]+)\.(\S+)\s*$", RegexOptions.Multiline);
    private static readonly Regex ToColumn = new(@"^\s*toColumn:\s*([^.\r\n]+)\.(\S+)\s*$", RegexOptions.Multiline);

    public ParsedSemanticModel Parse(IReadOnlyDictionary<string, string> filesByPath)
    {
        var tables = new List<ParsedTable>();

        foreach (var (path, content) in filesByPath)
        {
            if (!path.StartsWith("tables/", StringComparison.OrdinalIgnoreCase))
                continue;

            var tableMatch = TableDeclaration.Match(content);
            if (!tableMatch.Success)
                continue; // not a well-formed table file — let the caller's own validation surface this

            var tableName = tableMatch.Groups[1].Value;
            var (columns, measures) = ParseBlocks(content);
            tables.Add(new ParsedTable(tableName, columns, measures));
        }

        var relationships = new List<ParsedRelationship>();
        if (filesByPath.TryGetValue("relationships.tmdl", out var relContent))
        {
            foreach (var block in SplitOnBlockStart(relContent, RelationshipStart))
            {
                var from = FromColumn.Match(block);
                var to = ToColumn.Match(block);
                if (from.Success && to.Success)
                {
                    relationships.Add(new ParsedRelationship(
                        from.Groups[1].Value.Trim(), from.Groups[2].Value.Trim(),
                        to.Groups[1].Value.Trim(), to.Groups[2].Value.Trim()));
                }
            }
        }

        return new ParsedSemanticModel(tables, relationships);
    }

    private static (List<ParsedColumn> Columns, List<ParsedMeasure> Measures) ParseBlocks(string tableContent)
    {
        var columns = new List<ParsedColumn>();
        var measures = new List<ParsedMeasure>();

        foreach (var block in SplitOnBlockStart(tableContent, BlockStart))
        {
            var firstLine = block.Split('\n', 2)[0];

            var columnMatch = ColumnStart.Match(firstLine);
            if (columnMatch.Success)
            {
                var name = columnMatch.Groups[1].Value.Trim();
                var dataType = ExtractProperty(block, "dataType") ?? "string";
                var isKey = Regex.IsMatch(block, @"^\s*isKey\s*$", RegexOptions.Multiline);
                columns.Add(new ParsedColumn(name, dataType, isKey));
                continue;
            }

            var measureMatch = MeasureStart.Match(firstLine);
            if (measureMatch.Success)
            {
                var name = measureMatch.Groups[1].Value.Trim();
                var dax = measureMatch.Groups[2].Value.Trim();
                var formatString = ExtractProperty(block, "formatString");
                var displayFolder = ExtractProperty(block, "displayFolder");
                measures.Add(new ParsedMeasure(name, dax, formatString, displayFolder));
            }
        }

        return (columns, measures);
    }

    /// <summary>Splits raw TMDL text into blocks, each starting at a line matched by <paramref name="blockStart"/> and running until the next match (or end of text).</summary>
    private static List<string> SplitOnBlockStart(string content, Regex blockStart)
    {
        var matches = blockStart.Matches(content);
        var blocks = new List<string>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            blocks.Add(content[start..end]);
        }
        return blocks;
    }

    private static string? ExtractProperty(string block, string key)
    {
        var match = Regex.Match(block, $@"^\s*{Regex.Escape(key)}:\s*(.+?)\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
