namespace StbiBindDeploy.Api.Models;

/// <summary>
/// Publishes a freshly-assembled PBIP file set (semantic model TMDL + report.json + .platform)
/// as a real Power BI dataset+report with visuals — the packaging/import/gateway/refresh
/// mechanics ported from the external deploy_agent.py prototype's proven call sequence. Entirely
/// separate from <see cref="DeployDatasetRequest"/> (schema-only Push dataset, no visuals) and
/// <see cref="RebindTemplateRequest"/> (clones an already-published template) — this is the third,
/// from-scratch path: koru-main has already generated the TMDL and report.json itself.
/// </summary>
public sealed record PbipFileDto(string RelativePath, string Content);

/// <summary>
/// Mirrors Power BI's real refreshSchedule API shape (days/times/enabled/timezone) rather than
/// the Python prototype's Daily/Weekly/Monthly "Frequency" concept — Power BI's dataset refresh
/// schedule has no native "Monthly" option at all, so that part of the prototype was never
/// achievable via this endpoint; Days being a specific weekday subset already covers "weekly."
/// </summary>
public sealed record RefreshScheduleDto(bool Enabled, List<string> Days, List<string> Times, string TimeZone);

public sealed record PbipImportRequest(
    string ClientName,
    string ReportName,
    string WorkspaceName,
    List<PbipFileDto> Files,
    string? CapacityId = null,
    string? GatewayId = null,
    RefreshScheduleDto? RefreshSchedule = null);

public sealed record PbipImportResult(
    string WorkspaceId,
    string WorkspaceName,
    string DatasetId,
    string ReportId,
    bool GatewayBound,
    bool RefreshScheduleSet,
    bool RefreshTriggered,
    IReadOnlyList<string> Steps);
