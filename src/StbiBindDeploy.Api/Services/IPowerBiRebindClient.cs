namespace StbiBindDeploy.Api.Services;

/// <summary>
/// The three Power BI REST operations the template rebind flow needs, none of which
/// <see cref="IPowerBiClient"/> exposes today (it only ever creates schema-only Push-mode
/// datasets — see DeploymentService's remarks). Every real template in DashboardTemplateLibrary
/// is authored as an Import-mode dataset whose partitions read from a single M-query
/// "SourceFilePath" parameter (see any tables/*.tmdl under DashboardTemplateLibrary — e.g.
/// `Excel.Workbook(File.Contents(SourceFilePath), ...)`), which is exactly what Clone +
/// UpdateParameters + refresh operate on. Kept as its own interface/implementation rather than
/// extending IPowerBiClient so this stays additive — nothing about the existing dataset-create
/// path changes.
/// </summary>
public interface IPowerBiRebindClient
{
    /// <summary>Clones an existing dataset into (optionally) a different workspace. Returns the new dataset's id.</summary>
    Task<string> CloneDatasetAsync(string sourceWorkspaceId, string sourceDatasetId, string newDatasetName, string targetWorkspaceId, CancellationToken cancellationToken = default);

    /// <summary>Updates the dataset's "SourceFilePath" M-query parameter — the same parameter name every template TMDL partition already reads from.</summary>
    Task UpdateSourceFilePathParameterAsync(string workspaceId, string datasetId, string sourceFilePath, CancellationToken cancellationToken = default);

    /// <summary>Triggers an on-demand refresh. Fire-and-forget from Power BI's perspective — this returns once the refresh request is accepted, not once it completes.</summary>
    Task TriggerRefreshAsync(string workspaceId, string datasetId, CancellationToken cancellationToken = default);
}
