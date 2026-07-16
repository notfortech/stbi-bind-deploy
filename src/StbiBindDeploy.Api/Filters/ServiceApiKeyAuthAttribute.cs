using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using StbiBindDeploy.Api.Options;

namespace StbiBindDeploy.Api.Filters;

/// <summary>
/// Validates the X-Service-Api-Key header against Security:KoruApiKey. This service holds live
/// Power BI credentials and must never accept an unauthenticated request — every controller in
/// this service should carry this attribute (there is no "public" endpoint here, unlike
/// koru-main where only one internal-ingestion endpoint needed this treatment).
/// </summary>
public class ServiceApiKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<SecurityOptions>>().CurrentValue;

        if (string.IsNullOrWhiteSpace(options.KoruApiKey))
        {
            context.Result = new ObjectResult(new { error = "Service API key auth is not configured on this backend." })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        var provided = context.HttpContext.Request.Headers["X-Service-Api-Key"].ToString();
        if (string.IsNullOrEmpty(provided) || provided != options.KoruApiKey)
        {
            context.Result = new ObjectResult(new { error = "Invalid or missing X-Service-Api-Key." })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        await next();
    }
}
