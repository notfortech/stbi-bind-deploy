using Microsoft.AspNetCore.Mvc;
using StbiBindDeploy.Api.Filters;
using StbiBindDeploy.Api.Models;
using StbiBindDeploy.Api.Services;

namespace StbiBindDeploy.Api.Controllers;

/// <summary>
/// New, standalone endpoint — takes a fully-assembled PBIP file set (TMDL + report.json +
/// .platform, generated from scratch by koru-main) and publishes it as a real dataset+report.
/// Does not touch <see cref="DeploymentsController"/>'s Push-dataset path or
/// <see cref="TemplateRebindController"/>'s clone-an-existing-template path.
/// </summary>
[ApiController]
[Route("api/deployments")]
[ServiceApiKeyAuth]
public class PbipImportController : ControllerBase
{
    private readonly IPbipImportService _pbipImport;
    private readonly ILogger<PbipImportController> _logger;

    public PbipImportController(IPbipImportService pbipImport, ILogger<PbipImportController> logger)
    {
        _pbipImport = pbipImport;
        _logger = logger;
    }

    [HttpPost("pbip-import")]
    public async Task<ActionResult<PbipImportResult>> Import([FromBody] PbipImportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceName))
            return BadRequest(new { error = "workspaceName is required." });
        if (string.IsNullOrWhiteSpace(request.ReportName))
            return BadRequest(new { error = "reportName is required." });
        if (request.Files is null || request.Files.Count == 0)
            return BadRequest(new { error = "files must contain at least one PBIP file." });

        try
        {
            var result = await _pbipImport.ImportAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PowerBiApiException ex)
        {
            _logger.LogError(ex, "PBIP import failed for WorkspaceName={WorkspaceName} ReportName={ReportName}",
                request.WorkspaceName, request.ReportName);
            return Problem(title: "Power BI PBIP import failed", detail: ex.Message, statusCode: 502);
        }
    }
}
