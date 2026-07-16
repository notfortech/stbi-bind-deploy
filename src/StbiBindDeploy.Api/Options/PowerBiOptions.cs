namespace StbiBindDeploy.Api.Options;

/// <summary>
/// Power BI service-principal credentials. Same app registration already used by koru-main's
/// PowerBIService for embed tokens (see StudioTechBI.Backend/StudioTechBI.Application/Services/
/// PowerBIService.cs) — this service only needs its own copy of the same values, not a new
/// registration. CapacityId is optional: omit it to create workspaces on shared/Pro capacity.
/// </summary>
public class PowerBiOptions
{
    public const string SectionName = "PowerBI";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string? CapacityId { get; set; }
}
