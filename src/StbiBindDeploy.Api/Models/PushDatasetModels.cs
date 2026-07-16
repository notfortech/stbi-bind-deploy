namespace StbiBindDeploy.Api.Models;

public sealed record DatasetInfo(string Id, string Name);

public sealed record DeployDatasetRequest(
    string ClientName,
    string DatasetName,
    List<TmdlFileInput> TmdlFiles,
    string? CapacityId = null);

public sealed record TmdlFileInput(string Path, string Content);

public sealed record DeployDatasetResult(
    string WorkspaceId,
    string WorkspaceName,
    string DatasetId,
    string DatasetName,
    bool Created,
    IReadOnlyList<string> Steps);
