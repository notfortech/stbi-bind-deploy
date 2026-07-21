using StbiBindDeploy.Api.Models;

namespace StbiBindDeploy.Api.Services;

public interface IPbipImportService
{
    Task<PbipImportResult> ImportAsync(PbipImportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// New, wholly separate flow from <see cref="DeploymentService"/> and
/// <see cref="TemplateRebindService"/>: koru-main has already generated a full PBIP file set
/// (TMDL + report.json + .platform) from scratch — this publishes it as a real dataset+report,
/// optionally binds a gateway and sets a refresh schedule, then triggers an initial refresh.
/// Reuses <see cref="IPowerBiClient"/> for workspace resolution and
/// <see cref="IPowerBiRebindClient"/>'s TriggerRefreshAsync (byte-for-byte the same refresh
/// endpoint) rather than duplicating either.
/// </summary>
public sealed class PbipImportService : IPbipImportService
{
    private readonly IPowerBiClient _powerBi;
    private readonly IPbipImportClient _pbipClient;
    private readonly IPowerBiRebindClient _rebindClient;
    private readonly ILogger<PbipImportService> _logger;

    public PbipImportService(IPowerBiClient powerBi, IPbipImportClient pbipClient, IPowerBiRebindClient rebindClient, ILogger<PbipImportService> logger)
    {
        _powerBi = powerBi;
        _pbipClient = pbipClient;
        _rebindClient = rebindClient;
        _logger = logger;
    }

    public async Task<PbipImportResult> ImportAsync(PbipImportRequest request, CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        void Step(string message)
        {
            _logger.LogInformation("PbipImport.Step {Message}", message);
            steps.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {message}");
        }

        Step($"Resolving workspace '{request.WorkspaceName}'");
        var workspace = await _powerBi.GetWorkspaceByNameAsync(request.WorkspaceName, cancellationToken)
            ?? await _powerBi.CreateWorkspaceAsync(request.WorkspaceName, request.CapacityId, cancellationToken);
        Step($"Workspace ready: '{workspace.Name}' ({workspace.Id})");

        Step($"Building PBIP zip ({request.Files.Count} files)");
        var zipBytes = _pbipClient.BuildPbipZip(request.Files);

        Step("Importing PBIP package into Power BI (polls for up to ~60s)");
        var (datasetId, reportId) = await _pbipClient.ImportPbipZipAsync(workspace.Id, request.ReportName, zipBytes, cancellationToken);
        Step($"Import succeeded — DatasetId={datasetId} ReportId={reportId}");

        var gatewayBound = false;
        if (!string.IsNullOrWhiteSpace(request.GatewayId))
        {
            Step($"Binding dataset to gateway '{request.GatewayId}'");
            await _pbipClient.BindGatewayAsync(workspace.Id, datasetId, request.GatewayId, cancellationToken);
            gatewayBound = true;
            Step("Gateway bound");
        }
        else
        {
            Step("No gatewayId provided — skipping gateway bind; refresh only works if the data source is otherwise reachable (e.g. a SAS URL).");
        }

        var scheduleSet = false;
        if (request.RefreshSchedule is { Enabled: true } schedule)
        {
            Step("Setting refresh schedule");
            await _pbipClient.SetRefreshScheduleAsync(workspace.Id, datasetId, schedule, cancellationToken);
            scheduleSet = true;
            Step("Refresh schedule set");
        }

        Step("Triggering an initial refresh");
        await _rebindClient.TriggerRefreshAsync(workspace.Id, datasetId, cancellationToken);
        Step("Refresh triggered — Power BI processes it asynchronously");

        return new PbipImportResult(
            workspace.Id, workspace.Name, datasetId, reportId, gatewayBound, scheduleSet, RefreshTriggered: true, steps);
    }
}
