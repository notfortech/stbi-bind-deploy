namespace StbiBindDeploy.Api.Models;

/// <summary>
/// Rebinds a manually-authored template's published dataset into a specific client's own
/// workspace and points it at that client's data, so a matched template can go from "found by
/// TemplateMatchingService" to "a refreshable report in the client's workspace" without anyone
/// touching Power BI Desktop.
///
/// This is a wholly separate flow from <see cref="DeployDatasetRequest"/> — it never creates a
/// dataset from TMDL; it clones an existing, already-published template dataset (the one a human
/// built and registered via the admin template library) and rebinds the clone.
/// </summary>
public sealed record RebindTemplateRequest(
    string ClientName,
    string TemplateWorkspaceName,
    string TemplateDatasetName,
    string SourceFilePath,
    string? CapacityId = null);

public sealed record RebindTemplateResult(
    string WorkspaceId,
    string WorkspaceName,
    string DatasetId,
    string DatasetName,
    bool RefreshTriggered,
    IReadOnlyList<string> Steps);
