using Microsoft.AspNetCore.Mvc;
using StbiBindDeploy.Api.Filters;
using StbiBindDeploy.Api.Models;
using StbiBindDeploy.Api.Services;

namespace StbiBindDeploy.Api.Controllers;

/// <summary>
/// S6 scope: auth + workspace resolution only. Proves the service-principal credentials work
/// and that this service can get-or-create a client's dedicated Power BI workspace. Dataset and
/// report deploy land in S8, once S7 gives this service an actual TMDL artifact to deploy.
/// </summary>
[ApiController]
[Route("api/workspaces")]
[ServiceApiKeyAuth]
public class WorkspacesController : ControllerBase
{
    private readonly IPowerBiClient _powerBi;
    private readonly ILogger<WorkspacesController> _logger;

    public WorkspacesController(IPowerBiClient powerBi, ILogger<WorkspacesController> logger)
    {
        _powerBi = powerBi;
        _logger = logger;
    }

    /// <summary>Get-or-create the dedicated workspace for a client by name. Idempotent — safe to call repeatedly for the same ClientName.</summary>
    [HttpPost("resolve")]
    public async Task<ActionResult<WorkspaceInfo>> Resolve([FromBody] ResolveWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientName))
            return BadRequest(new { error = "clientName is required." });

        try
        {
            var existing = await _powerBi.GetWorkspaceByNameAsync(request.ClientName, cancellationToken);
            if (existing is not null)
                return Ok(existing);

            _logger.LogInformation("Workspaces.Resolve creating new workspace for ClientName={ClientName}", request.ClientName);
            var created = await _powerBi.CreateWorkspaceAsync(request.ClientName, request.CapacityId, cancellationToken);
            return Ok(created);
        }
        catch (PowerBiApiException ex)
        {
            _logger.LogError(ex, "Workspaces.Resolve failed for ClientName={ClientName}", request.ClientName);
            return Problem(title: "Power BI workspace resolution failed", detail: ex.Message, statusCode: 502);
        }
    }
}
