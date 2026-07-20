using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using StbiBindDeploy.Api.Options;

namespace StbiBindDeploy.Api.Services;

/// <summary>
/// Same auth pattern as <see cref="PowerBiClient"/> (app-only/service-principal MSAL against the
/// Power BI API scope, same PowerBiOptions), kept as an entirely separate class rather than
/// reusing PowerBiClient's private token logic so the template rebind flow has zero coupling to
/// (and zero risk of regressing) the existing dataset-create path.
/// </summary>
public sealed class PowerBiRebindClient : IPowerBiRebindClient
{
    private const string ApiBase = "https://api.powerbi.com/v1.0/myorg";
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly HttpClient _http;
    private readonly PowerBiOptions _options;
    private readonly ILogger<PowerBiRebindClient> _logger;
    private readonly IConfidentialClientApplication _msalApp;

    public PowerBiRebindClient(HttpClient http, IOptions<PowerBiOptions> options, ILogger<PowerBiRebindClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        _msalApp = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
            .Build();
    }

    public async Task<string> CloneDatasetAsync(string sourceWorkspaceId, string sourceDatasetId, string newDatasetName, string targetWorkspaceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "PowerBiRebind.CloneDataset.Requested SourceWorkspaceId={SourceWorkspaceId} SourceDatasetId={SourceDatasetId} TargetWorkspaceId={TargetWorkspaceId}",
            sourceWorkspaceId, sourceDatasetId, targetWorkspaceId);

        var payload = JsonSerializer.Serialize(new { name = newDatasetName, targetWorkspaceId });
        using var response = await SendAsync(HttpMethod.Post,
            $"{ApiBase}/groups/{sourceWorkspaceId}/datasets/{sourceDatasetId}/Default.Clone", payload, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PowerBiRebind.CloneDataset.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(json));
            throw new PowerBiApiException($"Power BI clone-dataset call failed: {(int)response.StatusCode} {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var newDatasetId = doc.RootElement.GetProperty("id").GetString()!;
        _logger.LogInformation("PowerBiRebind.CloneDataset.Succeeded NewDatasetId={NewDatasetId}", newDatasetId);
        return newDatasetId;
    }

    public async Task UpdateSourceFilePathParameterAsync(string workspaceId, string datasetId, string sourceFilePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PowerBiRebind.UpdateParameters.Requested WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);

        var payload = JsonSerializer.Serialize(new
        {
            updateDetails = new[] { new { name = "SourceFilePath", newValue = sourceFilePath } }
        });
        using var response = await SendAsync(HttpMethod.Post,
            $"{ApiBase}/groups/{workspaceId}/datasets/{datasetId}/Default.UpdateParameters", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("PowerBiRebind.UpdateParameters.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(body));
            throw new PowerBiApiException($"Power BI update-parameters call failed: {(int)response.StatusCode} {Truncate(body)}");
        }

        _logger.LogInformation("PowerBiRebind.UpdateParameters.Succeeded WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);
    }

    public async Task TriggerRefreshAsync(string workspaceId, string datasetId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PowerBiRebind.TriggerRefresh.Requested WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);

        var payload = JsonSerializer.Serialize(new { notifyOption = "NoNotification" });
        using var response = await SendAsync(HttpMethod.Post,
            $"{ApiBase}/groups/{workspaceId}/datasets/{datasetId}/refreshes", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("PowerBiRebind.TriggerRefresh.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(body));
            throw new PowerBiApiException($"Power BI trigger-refresh call failed: {(int)response.StatusCode} {Truncate(body)}");
        }

        _logger.LogInformation("PowerBiRebind.TriggerRefresh.Succeeded WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? body, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await _http.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _msalApp.AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            _logger.LogError(ex, "PowerBiRebind.Auth.Failed ErrorCode={ErrorCode}", ex.ErrorCode);
            throw new PowerBiApiException("Power BI authentication failed — check TenantId/ClientId/ClientSecret and the service principal's Power BI API permissions.", ex);
        }
    }

    private static string Truncate(string s, int max = 500) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
