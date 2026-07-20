using StbiBindDeploy.Api.Models;

namespace StbiBindDeploy.Api.Services;

public interface ITemplateRebindService
{
    Task<RebindTemplateResult> RebindAsync(RebindTemplateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// New, wholly separate flow from <see cref="DeploymentService"/>: given a template that's
/// already published as a real dataset (a human built it in Power BI Desktop and registered it
/// through the admin template library — see BLUEPRINT_TEMPLATE_FORMAT.md in stbi_transformers),
/// clone it into the client's own workspace, point the clone at the client's data, and refresh.
///
/// Reuses <see cref="IPowerBiClient"/> unmodified for workspace/dataset lookup — the only new
/// Power BI operations this needs (clone/update-parameters/refresh) live in the new
/// <see cref="IPowerBiRebindClient"/> instead of being added to the existing client.
/// </summary>
public sealed class TemplateRebindService : ITemplateRebindService
{
    private readonly IPowerBiClient _powerBi;
    private readonly IPowerBiRebindClient _rebindClient;
    private readonly ILogger<TemplateRebindService> _logger;

    public TemplateRebindService(IPowerBiClient powerBi, IPowerBiRebindClient rebindClient, ILogger<TemplateRebindService> logger)
    {
        _powerBi = powerBi;
        _rebindClient = rebindClient;
        _logger = logger;
    }

    public async Task<RebindTemplateResult> RebindAsync(RebindTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        void Step(string message)
        {
            _logger.LogInformation("TemplateRebind.Step {Message}", message);
            steps.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {message}");
        }

        Step($"Resolving template workspace '{request.TemplateWorkspaceName}'");
        var templateWorkspace = await _powerBi.GetWorkspaceByNameAsync(request.TemplateWorkspaceName, cancellationToken)
            ?? throw new InvalidOperationException($"Template workspace '{request.TemplateWorkspaceName}' not found.");

        Step($"Resolving template dataset '{request.TemplateDatasetName}'");
        var templateDataset = await _powerBi.GetDatasetByNameAsync(templateWorkspace.Id, request.TemplateDatasetName, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Template dataset '{request.TemplateDatasetName}' not found in workspace '{request.TemplateWorkspaceName}' — " +
                "has this template actually been published to Power BI yet?");

        Step($"Resolving workspace for client '{request.ClientName}'");
        var clientWorkspace = await _powerBi.GetWorkspaceByNameAsync(request.ClientName, cancellationToken)
            ?? await _powerBi.CreateWorkspaceAsync(request.ClientName, request.CapacityId, cancellationToken);
        Step($"Client workspace ready: '{clientWorkspace.Name}' ({clientWorkspace.Id})");

        var cloneName = $"{request.TemplateDatasetName} ({request.ClientName})";
        Step($"Cloning '{request.TemplateDatasetName}' into '{clientWorkspace.Name}' as '{cloneName}'");
        var newDatasetId = await _rebindClient.CloneDatasetAsync(
            templateWorkspace.Id, templateDataset.Id, cloneName, clientWorkspace.Id, cancellationToken);
        Step($"Clone created: {newDatasetId}");

        Step("Pointing the clone's SourceFilePath parameter at the client's data");
        await _rebindClient.UpdateSourceFilePathParameterAsync(clientWorkspace.Id, newDatasetId, request.SourceFilePath, cancellationToken);

        Step("Triggering refresh");
        await _rebindClient.TriggerRefreshAsync(clientWorkspace.Id, newDatasetId, cancellationToken);
        Step("Refresh triggered — Power BI processes it asynchronously");

        return new RebindTemplateResult(
            clientWorkspace.Id, clientWorkspace.Name, newDatasetId, cloneName, RefreshTriggered: true, steps);
    }
}
