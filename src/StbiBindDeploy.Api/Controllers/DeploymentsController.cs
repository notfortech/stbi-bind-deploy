using Microsoft.AspNetCore.Mvc;
using StbiBindDeploy.Api.Filters;
using StbiBindDeploy.Api.Models;
using StbiBindDeploy.Api.Services;

namespace StbiBindDeploy.Api.Controllers;

/// <summary>
/// S8 (part 2 of 2 — part 1 is stbi_transformers' deterministic TMDL validator). Takes TMDL
/// files (expected to have already passed that validator) and deploys them as a Power BI Push
/// dataset. No LLM call happens anywhere in this path — every step here is deterministic.
/// </summary>
[ApiController]
[Route("api/deployments")]
[ServiceApiKeyAuth]
public class DeploymentsController : ControllerBase
{
    private readonly IDeploymentService _deployment;
    private readonly ILogger<DeploymentsController> _logger;

    public DeploymentsController(IDeploymentService deployment, ILogger<DeploymentsController> logger)
    {
        _deployment = deployment;
        _logger = logger;
    }

    [HttpPost("dataset")]
    public async Task<ActionResult<DeployDatasetResult>> DeployDataset([FromBody] DeployDatasetRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientName))
            return BadRequest(new { error = "clientName is required." });
        if (string.IsNullOrWhiteSpace(request.DatasetName))
            return BadRequest(new { error = "datasetName is required." });
        if (request.TmdlFiles is null || request.TmdlFiles.Count == 0)
            return BadRequest(new { error = "tmdlFiles must contain at least one file." });

        try
        {
            var result = await _deployment.DeployDatasetAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PowerBiApiException ex)
        {
            _logger.LogError(ex, "Dataset deployment failed for ClientName={ClientName} DatasetName={DatasetName}", request.ClientName, request.DatasetName);
            return Problem(title: "Power BI dataset deployment failed", detail: ex.Message, statusCode: 502);
        }
    }
}
