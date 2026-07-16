using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using StbiBindDeploy.Api.Models;
using StbiBindDeploy.Api.Options;

namespace StbiBindDeploy.Api.Services;

/// <summary>
/// Talks to the Power BI REST API using app-only (service-principal) auth — same auth pattern
/// already proven working in koru-main's PowerBIService.GetAccessToken() (MSAL
/// ConfidentialClientApplication against the analysis.windows.net/powerbi/api scope), reused
/// here rather than a new auth path, since it's the same app registration.
///
/// Requires the tenant's Power BI Admin Portal to have "Allow service principals to use Power BI
/// APIs" (and, for workspace creation specifically, "Allow service principals to create
/// workspaces") enabled for this app's security group — a one-time tenant-admin action, not
/// something this code can do for itself.
/// </summary>
public sealed class PowerBiClient : IPowerBiClient
{
    private const string ApiBase = "https://api.powerbi.com/v1.0/myorg";
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly HttpClient _http;
    private readonly PowerBiOptions _options;
    private readonly ILogger<PowerBiClient> _logger;
    private readonly IConfidentialClientApplication _msalApp;

    public PowerBiClient(HttpClient http, IOptions<PowerBiOptions> options, ILogger<PowerBiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret) || string.IsNullOrWhiteSpace(_options.TenantId))
            _logger.LogWarning("PowerBI credentials are not fully configured (TenantId/ClientId/ClientSecret) — calls will fail until they are.");

        _msalApp = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
            .Build();
    }

    public async Task<WorkspaceInfo?> GetWorkspaceByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        // OData filter — single quotes inside the name must be doubled per OData string-literal escaping.
        var escaped = name.Replace("'", "''");
        var url = $"{ApiBase}/groups?$filter=name eq '{Uri.EscapeDataString(escaped)}'";

        _logger.LogInformation("PowerBI.GetWorkspaceByName.Requested Name={Name}", name);
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PowerBI.GetWorkspaceByName.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(json));
            throw new PowerBiApiException($"Power BI list-workspaces call failed: {(int)response.StatusCode} {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var groups = doc.RootElement.GetProperty("value");
        foreach (var group in groups.EnumerateArray())
        {
            var groupName = group.GetProperty("name").GetString();
            if (string.Equals(groupName, name, StringComparison.OrdinalIgnoreCase))
            {
                var found = new WorkspaceInfo(group.GetProperty("id").GetString()!, groupName!);
                _logger.LogInformation("PowerBI.GetWorkspaceByName.Found WorkspaceId={WorkspaceId}", found.Id);
                return found;
            }
        }

        _logger.LogInformation("PowerBI.GetWorkspaceByName.NotFound Name={Name}", name);
        return null;
    }

    public async Task<WorkspaceInfo> CreateWorkspaceAsync(string name, string? capacityId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PowerBI.CreateWorkspace.Requested Name={Name}", name);

        var payload = JsonSerializer.Serialize(new { name });
        using var response = await SendAsync(HttpMethod.Post, $"{ApiBase}/groups?workspaceV2=true", payload, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PowerBI.CreateWorkspace.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(json));
            throw new PowerBiApiException($"Power BI create-workspace call failed: {(int)response.StatusCode} {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var workspace = new WorkspaceInfo(
            doc.RootElement.GetProperty("id").GetString()!,
            doc.RootElement.GetProperty("name").GetString()!);

        _logger.LogInformation("PowerBI.CreateWorkspace.Succeeded WorkspaceId={WorkspaceId}", workspace.Id);

        var effectiveCapacityId = capacityId ?? _options.CapacityId;
        if (!string.IsNullOrWhiteSpace(effectiveCapacityId))
            await AssignToCapacityAsync(workspace.Id, effectiveCapacityId, cancellationToken);

        return workspace;
    }

    public async Task<DatasetInfo?> GetDatasetByNameAsync(string workspaceId, string name, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PowerBI.GetDatasetByName.Requested WorkspaceId={WorkspaceId} Name={Name}", workspaceId, name);
        using var response = await SendAsync(HttpMethod.Get, $"{ApiBase}/groups/{workspaceId}/datasets", body: null, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PowerBI.GetDatasetByName.Failed StatusCode={StatusCode} Body={Body}", (int)response.StatusCode, Truncate(json));
            throw new PowerBiApiException($"Power BI list-datasets call failed: {(int)response.StatusCode} {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var ds in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var dsName = ds.GetProperty("name").GetString();
            if (string.Equals(dsName, name, StringComparison.OrdinalIgnoreCase))
            {
                var found = new DatasetInfo(ds.GetProperty("id").GetString()!, dsName!);
                _logger.LogInformation("PowerBI.GetDatasetByName.Found DatasetId={DatasetId}", found.Id);
                return found;
            }
        }

        _logger.LogInformation("PowerBI.GetDatasetByName.NotFound WorkspaceId={WorkspaceId} Name={Name}", workspaceId, name);
        return null;
    }

    public async Task<DatasetInfo> CreateDatasetAsync(string workspaceId, string name, ParsedSemanticModel model, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "PowerBI.CreateDataset.Requested WorkspaceId={WorkspaceId} Name={Name} TableCount={TableCount} RelationshipCount={RelationshipCount}",
            workspaceId, name, model.Tables.Count, model.Relationships.Count);

        var payload = JsonSerializer.Serialize(new
        {
            name,
            defaultMode = "Push",
            tables = model.Tables.Select(t => new
            {
                name = t.Name,
                columns = t.Columns.Select(c => new { name = c.Name, dataType = MapDataType(c.DataType) }),
                measures = t.Measures.Count == 0
                    ? null
                    : t.Measures.Select(m => new { name = m.Name, expression = m.Dax, formatString = m.FormatString })
            }),
            relationships = model.Relationships.Select(r => new
            {
                name = $"{r.FromTable}_{r.FromColumn}_{r.ToTable}_{r.ToColumn}",
                fromTable = r.FromTable,
                fromColumn = r.FromColumn,
                toTable = r.ToTable,
                toColumn = r.ToColumn
            })
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        using var response = await SendAsync(HttpMethod.Post, $"{ApiBase}/groups/{workspaceId}/datasets", payload, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PowerBI.CreateDataset.Failed WorkspaceId={WorkspaceId} StatusCode={StatusCode} Body={Body}",
                workspaceId, (int)response.StatusCode, Truncate(json));
            throw new PowerBiApiException($"Power BI create-dataset call failed: {(int)response.StatusCode} {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var dataset = new DatasetInfo(doc.RootElement.GetProperty("id").GetString()!, name);
        _logger.LogInformation("PowerBI.CreateDataset.Succeeded WorkspaceId={WorkspaceId} DatasetId={DatasetId}", workspaceId, dataset.Id);
        return dataset;
    }

    /// <summary>TMDL dataType -> Power BI Push Dataset API dataType. Unrecognized values fall back to String rather than failing the whole deploy over a naming mismatch.</summary>
    private static string MapDataType(string tmdlDataType) => tmdlDataType.Trim().ToLowerInvariant() switch
    {
        "int64" or "integer" or "int" => "Int64",
        "double" or "decimal" or "number" => "Double",
        "boolean" or "bool" => "Boolean",
        "datetime" or "date" => "DateTime",
        _ => "String"
    };

    private async Task AssignToCapacityAsync(string workspaceId, string capacityId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("PowerBI.AssignToCapacity.Requested WorkspaceId={WorkspaceId} CapacityId={CapacityId}", workspaceId, capacityId);

        var payload = JsonSerializer.Serialize(new { capacityId });
        using var response = await SendAsync(HttpMethod.Post, $"{ApiBase}/groups/{workspaceId}/AssignToCapacity", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Non-fatal — the workspace exists and is usable on shared capacity even if this fails.
            _logger.LogWarning("PowerBI.AssignToCapacity.Failed WorkspaceId={WorkspaceId} StatusCode={StatusCode} Body={Body}",
                workspaceId, (int)response.StatusCode, Truncate(body));
            return;
        }

        _logger.LogInformation("PowerBI.AssignToCapacity.Succeeded WorkspaceId={WorkspaceId}", workspaceId);
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
            _logger.LogError(ex, "PowerBI.Auth.Failed ErrorCode={ErrorCode}", ex.ErrorCode);
            throw new PowerBiApiException("Power BI authentication failed — check TenantId/ClientId/ClientSecret and the service principal's Power BI API permissions.", ex);
        }
    }

    private static string Truncate(string s, int max = 500) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}

public sealed class PowerBiApiException : Exception
{
    public PowerBiApiException(string message, Exception? inner = null) : base(message, inner) { }
}
