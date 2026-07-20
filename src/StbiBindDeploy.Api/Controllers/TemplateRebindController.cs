using Microsoft.AspNetCore.Mvc;
using StbiBindDeploy.Api.Filters;
using StbiBindDeploy.Api.Models;
using StbiBindDeploy.Api.Services;

namespace StbiBindDeploy.Api.Controllers;

/// <summary>
/// New, standalone endpoint — takes a template that TemplateMatchingService (koru-main) has
/// already matched to a client's schema and a manually-published Power BI dataset, and produces
/// a refreshable dataset in that client's own workspace. Does not touch
/// <see cref="DeploymentsController"/>'s TMDL-based create path at all.
/// </summary>
[ApiController]
[Route("api/templates")]
[ServiceApiKeyAuth]
public class TemplateRebindController : ControllerBase
{
    private readonly ITemplateRebindService _rebind;
    private readonly ILogger<TemplateRebindController> _logger;

    public TemplateRebindController(ITemplateRebindService rebind, ILogger<TemplateRebindController> logger)
    {
        _rebind = rebind;
        _logger = logger;
    }

    [HttpPost("rebind")]
    public async Task<ActionResult<RebindTemplateResult>> Rebind([FromBody] RebindTemplateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientName))
            return BadRequest(new { error = "clientName is required." });
        if (string.IsNullOrWhiteSpace(request.TemplateWorkspaceName))
            return BadRequest(new { error = "templateWorkspaceName is required." });
        if (string.IsNullOrWhiteSpace(request.TemplateDatasetName))
            return BadRequest(new { error = "templateDatasetName is required." });
        if (string.IsNullOrWhiteSpace(request.SourceFilePath))
            return BadRequest(new { error = "sourceFilePath is required." });

        try
        {
            var result = await _rebind.RebindAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PowerBiApiException ex)
        {
            _logger.LogError(ex, "Template rebind failed for ClientName={ClientName} TemplateDatasetName={TemplateDatasetName}",
                request.ClientName, request.TemplateDatasetName);
            return Problem(title: "Power BI template rebind failed", detail: ex.Message, statusCode: 502);
        }
    }
}
