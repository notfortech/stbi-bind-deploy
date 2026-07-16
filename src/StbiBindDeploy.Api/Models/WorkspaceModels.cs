namespace StbiBindDeploy.Api.Models;

public sealed record WorkspaceInfo(string Id, string Name);

/// <summary>
/// Resolves (or creates) the dedicated Power BI workspace for one client. ClientName should be
/// a stable, human-readable identifier (e.g. "Client - Acme Pty Ltd") — the same name is looked
/// up on every call, so it must be deterministic per client, not regenerated per request.
/// </summary>
public sealed record ResolveWorkspaceRequest(string ClientName, string? CapacityId = null);
