namespace StbiBindDeploy.Api.Models;

public sealed record ParsedColumn(string Name, string DataType, bool IsKey);
public sealed record ParsedMeasure(string Name, string Dax, string? FormatString, string? DisplayFolder);

public sealed record ParsedTable(string Name, List<ParsedColumn> Columns, List<ParsedMeasure> Measures)
{
    public bool IsMeasuresOnly => Name.Equals("_Measures", StringComparison.OrdinalIgnoreCase);
}

public sealed record ParsedRelationship(string FromTable, string FromColumn, string ToTable, string ToColumn);

public sealed record ParsedSemanticModel(List<ParsedTable> Tables, List<ParsedRelationship> Relationships);
