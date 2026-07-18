using StbiBindDeploy.Api.Models;

namespace StbiBindDeploy.Api.Services;

public interface IDeploymentService
{
    Task<DeployDatasetResult> DeployDatasetAsync(DeployDatasetRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates S6 (workspace resolution) + the TMDL parser + Push Dataset API to deploy a
/// dataset from TMDL files. Every step is logged as it happens (not accumulated in memory and
/// flushed once at the end) — a crash mid-deployment should never lose the record of what
/// already succeeded, the specific gap this session's review of a prior reference
/// implementation flagged.
///
/// Known limitation: the Power BI Push Dataset API has no true "update an existing dataset's
/// schema" operation. If a dataset with this name already exists in the workspace, this returns
/// it as-is rather than attempting to reconcile a changed schema — re-deploying a genuinely
/// different TMDL shape onto an existing dataset name requires deleting and recreating it, which
/// this does not do automatically (a schema change is exactly the kind of action that shouldn't
/// happen silently).
/// </summary>
public sealed class DeploymentService : IDeploymentService
{
    private readonly IPowerBiClient _powerBi;
    private readonly ITmdlParser _parser;
    private readonly ILogger<DeploymentService> _logger;

    public DeploymentService(IPowerBiClient powerBi, ITmdlParser parser, ILogger<DeploymentService> logger)
    {
        _powerBi = powerBi;
        _parser = parser;
        _logger = logger;
    }

    public async Task<DeployDatasetResult> DeployDatasetAsync(DeployDatasetRequest request, CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        void Step(string message)
        {
            _logger.LogInformation("Deployment.Step {Message}", message);
            steps.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {message}");
        }

        Step($"Resolving workspace for client '{request.ClientName}'");
        var existingWorkspace = await _powerBi.GetWorkspaceByNameAsync(request.ClientName, cancellationToken);
        var workspace = existingWorkspace ?? await _powerBi.CreateWorkspaceAsync(request.ClientName, request.CapacityId, cancellationToken);
        Step($"Workspace ready: '{workspace.Name}' ({workspace.Id})");

        Step("Parsing TMDL files");
        var filesByPath = request.TmdlFiles.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase);
        var parsed = _parser.Parse(filesByPath);
        Step($"Parsed {parsed.Tables.Count} table(s), {parsed.Relationships.Count} relationship(s)");

        FoldMeasuresOnlyTables(parsed, Step);

        if (parsed.Tables.Count == 0)
            throw new InvalidOperationException("No tables could be parsed from the supplied TMDL files — nothing to deploy.");

        Step($"Checking for an existing dataset named '{request.DatasetName}'");
        var existingDataset = await _powerBi.GetDatasetByNameAsync(workspace.Id, request.DatasetName, cancellationToken);

        if (existingDataset is not null)
        {
            Step($"Dataset '{request.DatasetName}' already exists ({existingDataset.Id}) — reusing as-is, schema not reconciled");
            return new DeployDatasetResult(workspace.Id, workspace.Name, existingDataset.Id, existingDataset.Name, Created: false, steps);
        }

        Step($"Creating dataset '{request.DatasetName}'");
        var dataset = await _powerBi.CreateDatasetAsync(workspace.Id, request.DatasetName, parsed, cancellationToken);
        Step($"Dataset created: {dataset.Id}");

        return new DeployDatasetResult(workspace.Id, workspace.Name, dataset.Id, dataset.Name, Created: true, steps);
    }

    /// <summary>
    /// stbi_transformers' TmdlAuthoringService always emits a tables/_Measures.tmdl "island"
    /// table holding every DAX measure, with source = {BLANK()} and deliberately zero columns —
    /// standard TMDL practice for keeping measures organizationally separate from data tables.
    /// The Power BI Push Dataset API has no equivalent concept and rejects any table with an
    /// empty columns array ("Table must contain at least one column."), so a columnless
    /// measures-only table can never be pushed as-is. Fold its measures onto the first real
    /// (column-bearing) table instead and drop the empty table from the payload — this loses
    /// nothing since Power BI measures aren't meaningfully scoped to "their" table in reports.
    /// </summary>
    private static void FoldMeasuresOnlyTables(ParsedSemanticModel parsed, Action<string> step)
    {
        var measuresOnlyTables = parsed.Tables.Where(t => t.IsMeasuresOnly && t.Columns.Count == 0).ToList();
        if (measuresOnlyTables.Count == 0)
            return;

        var target = parsed.Tables.FirstOrDefault(t => !t.IsMeasuresOnly && t.Columns.Count > 0);

        foreach (var table in measuresOnlyTables)
        {
            parsed.Tables.Remove(table);
            if (target is not null)
            {
                target.Measures.AddRange(table.Measures);
                step($"Folded {table.Measures.Count} measure(s) from '{table.Name}' onto '{target.Name}' " +
                     "— Power BI's Push Dataset API can't host a columnless measures-only table");
            }
            else
            {
                step($"Dropped measures-only table '{table.Name}' — no other table with columns available " +
                     $"to host its {table.Measures.Count} measure(s)");
            }
        }
    }
}
