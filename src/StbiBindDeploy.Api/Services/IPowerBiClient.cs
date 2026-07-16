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
}
