using StbiBindDeploy.Api.Models;

namespace StbiBindDeploy.Api.Services;

/// <summary>
/// Power BI REST API surface this service needs. Deliberately narrow for S6 (auth + workspace
/// resolution only) — dataset/report deploy, refresh scheduling, and permissions land in S8
/// once there's an actual artifact (TMDL, from S7) to deploy.
/// </summary>
public interface IPowerBiClient
{
    Task<WorkspaceInfo?> GetWorkspaceByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<WorkspaceInfo> CreateWorkspaceAsync(string name, string? capacityId, CancellationToken cancellationToken = default);

    /// <summary>Push Dataset API only supports name-based lookup, not true schema-aware "update" — see DeploymentService for how this is used.</summary>
    Task<DatasetInfo?> GetDatasetByNameAsync(string workspaceId, string name, CancellationToken cancellationToken = default);
    Task<DatasetInfo> CreateDatasetAsync(string workspaceId, string name, ParsedSemanticModel model, CancellationToken cancellationToken = default);
}
