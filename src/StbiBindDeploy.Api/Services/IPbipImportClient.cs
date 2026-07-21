using StbiBindDeploy.Api.Models;

namespace StbiBindDeploy.Api.Services;

/// <summary>
/// The Power BI Import API surface needed to publish a freshly-assembled PBIP file set as a real
/// dataset+report with visuals — ported from the external deploy_agent.py prototype's proven call
/// sequence (POST .../imports, poll .../imports/{id}, Default.BindToGateway, PATCH
/// .../refreshSchedule). A third, independent capability alongside <see cref="IPowerBiClient"/>
/// (schema-only Push datasets) and <see cref="IPowerBiRebindClient"/> (clone+rebind an existing
/// published template) — not a replacement for either.
/// Unverified against a live tenant in this sandbox — no .NET SDK, no Power BI tenant available
/// here; every request shape below matches Microsoft's documented Import/refresh-schedule API,
/// not something exercised end-to-end this session.
/// </summary>
public interface IPbipImportClient
{
    /// <summary>Zips the given files in-memory (mirrors the Python prototype's zipfile step).</summary>
    byte[] BuildPbipZip(List<PbipFileDto> files);

    /// <summary>
    /// POSTs the zip to Power BI's Import API and polls until the import finishes. Unlike the
    /// Python prototype (which re-fetches dataset/report IDs via a second substring-matched list
    /// call), this reads them directly off the final poll response's own datasets/reports arrays.
    /// </summary>
    Task<(string DatasetId, string ReportId)> ImportPbipZipAsync(
        string workspaceId, string reportDisplayName, byte[] zipBytes, CancellationToken cancellationToken = default);

    /// <summary>Binds the dataset to an on-premises/VNet gateway. Only call this when a gatewayId
    /// is actually available — this method does not silently no-op like the Python prototype did.</summary>
    Task BindGatewayAsync(string workspaceId, string datasetId, string gatewayId, CancellationToken cancellationToken = default);

    /// <summary>Sets the dataset's scheduled refresh (days/times/timezone).</summary>
    Task SetRefreshScheduleAsync(string workspaceId, string datasetId, RefreshScheduleDto schedule, CancellationToken cancellationToken = default);
}
