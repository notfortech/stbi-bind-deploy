using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using StbiBindDeploy.Api.Models;
using StbiBindDeploy.Api.Options;

namespace StbiBindDeploy.Api.Services;

/// <summary>
/// Same auth pattern as <see cref="PowerBiClient"/>/<see cref="PowerBiRebindClient"/> (app-only
/// MSAL against the Power BI API scope, same PowerBiOptions), kept as its own class for the same
/// reason PowerBiRebindClient is: zero coupling to, zero risk of regressing, the existing paths.
/// </summary>
public sealed class PbipImportClient : IPbipImportClient
{
    private const string ApiBase = "https://api.powerbi.com/v1.0/myorg";
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];
    private const int MaxPollAttempts = 20;
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly PowerBiOptions _options;
    private readonly ILogger<PbipImportClient> _logger;
    private readonly IConfidentialClientApplication _msalApp;

    public PbipImportClient(HttpClient http, IOptions<PowerBiOptions> options, ILogger<PbipImportClient> logger)
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

    public byte[] BuildPbipZip(List<PbipFileDto> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.RelativePath.Replace('\\', '/'), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
                writer.Write(file.Content);
            }
        }
        return ms.ToArray();
    }

    public async Task<(string DatasetId, string ReportId)> ImportPbipZipAsync(
        string workspaceId, string reportDisplayName, byte[] zipBytes, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "PbipImport.Import.Requested WorkspaceId={WorkspaceId} ReportDisplayName={ReportDisplayName} SizeBytes={SizeBytes}",
            workspaceId, reportDisplayName, zipBytes.Length);

        var url = $"{ApiBase}/groups/{workspaceId}/imports" +
                   $"?datasetDisplayName={Uri.EscapeDataString(reportDisplayName)}&nameConflict=Overwrite&skipReport=false";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(zipBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(fileContent, "file", "report.zip");

        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PbipImport.Import.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(body));
            throw new PowerBiApiException($"Power BI PBIP import call failed: {(int)response.StatusCode} {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var importId = doc.RootElement.GetProperty("id").GetString()!;
        _logger.LogInformation("PbipImport.Import.Accepted ImportId={ImportId}", importId);

        return await PollImportAsync(workspaceId, importId, cancellationToken);
    }

    private async Task<(string DatasetId, string ReportId)> PollImportAsync(string workspaceId, string importId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxPollAttempts; attempt++)
        {
            await Task.Delay(PollDelay, cancellationToken);

            using var response = await SendAsync(HttpMethod.Get, $"{ApiBase}/groups/{workspaceId}/imports/{importId}", body: null, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("PbipImport.Poll.Failed Attempt={Attempt} StatusCode={StatusCode} Body={Body}", attempt, (int)response.StatusCode, Truncate(json));
                throw new PowerBiApiException($"Power BI import poll failed: {(int)response.StatusCode} {Truncate(json)}");
            }

            using var doc = JsonDocument.Parse(json);
            var state = doc.RootElement.TryGetProperty("importState", out var stateEl) ? stateEl.GetString() : null;
            _logger.LogInformation("PbipImport.Poll.State Attempt={Attempt} ImportId={ImportId} State={State}", attempt, importId, state);

            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new PowerBiApiException($"Power BI import failed: {Truncate(json)}");

            if (string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                var datasetId = doc.RootElement.TryGetProperty("datasets", out var datasets) && datasets.GetArrayLength() > 0
                    ? datasets[0].GetProperty("id").GetString()
                    : null;
                var reportId = doc.RootElement.TryGetProperty("reports", out var reports) && reports.GetArrayLength() > 0
                    ? reports[0].GetProperty("id").GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(datasetId) || string.IsNullOrWhiteSpace(reportId))
                    throw new PowerBiApiException($"Power BI import succeeded but did not return dataset/report IDs: {Truncate(json)}");

                _logger.LogInformation("PbipImport.Import.Succeeded DatasetId={DatasetId} ReportId={ReportId}", datasetId, reportId);
                return (datasetId, reportId);
            }
        }

        throw new PowerBiApiException($"Power BI import did not complete after {MaxPollAttempts} polling attempts ({MaxPollAttempts * PollDelay.TotalSeconds}s).");
    }

    public async Task BindGatewayAsync(string workspaceId, string datasetId, string gatewayId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PbipImport.BindGateway.Requested WorkspaceId={WorkspaceId} DatasetId={DatasetId} GatewayId={GatewayId}", workspaceId, datasetId, gatewayId);

        var payload = JsonSerializer.Serialize(new { gatewayObjectId = gatewayId });
        using var response = await SendAsync(HttpMethod.Post,
            $"{ApiBase}/groups/{workspaceId}/datasets/{datasetId}/Default.BindToGateway", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("PbipImport.BindGateway.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(body));
            throw new PowerBiApiException($"Power BI bind-gateway call failed: {(int)response.StatusCode} {Truncate(body)}");
        }

        _logger.LogInformation("PbipImport.BindGateway.Succeeded WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);
    }

    public async Task SetRefreshScheduleAsync(string workspaceId, string datasetId, RefreshScheduleDto schedule, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PbipImport.SetRefreshSchedule.Requested WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);

        var payload = JsonSerializer.Serialize(new
        {
            value = new
            {
                enabled = schedule.Enabled,
                days = schedule.Days,
                times = schedule.Times,
                localTimeZoneId = schedule.TimeZone
            }
        });
        using var response = await SendAsync(HttpMethod.Patch,
            $"{ApiBase}/groups/{workspaceId}/datasets/{datasetId}/refreshSchedule", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("PbipImport.SetRefreshSchedule.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(body));
            throw new PowerBiApiException($"Power BI set-refresh-schedule call failed: {(int)response.StatusCode} {Truncate(body)}");
        }

        _logger.LogInformation("PbipImport.SetRefreshSchedule.Succeeded WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, datasetId);
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
            _logger.LogError(ex, "PbipImport.Auth.Failed ErrorCode={ErrorCode}", ex.ErrorCode);
            throw new PowerBiApiException("Power BI authentication failed — check TenantId/ClientId/ClientSecret and the service principal's Power BI API permissions.", ex);
        }
    }

    private static string Truncate(string s, int max = 500) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
