namespace StbiBindDeploy.Api.Options;

/// <summary>
/// Shared secret validating inbound calls from koru-main — this service holds live Power BI
/// credentials and must never be reachable by an unauthenticated caller. Same pattern as
/// koru-main's ServiceApiKeyAuthAttribute (StudioTechBI.API/Filters), just the other side of it.
/// </summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Expected value of the X-Service-Api-Key header on every request. Set to the same value koru-main sends.</summary>
    public string KoruApiKey { get; set; } = string.Empty;
}
