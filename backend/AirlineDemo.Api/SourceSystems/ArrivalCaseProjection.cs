using System.Text.Json;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.SourceSystems;

public static class ArrivalCaseProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static object Create(CaseState state, string role, ArrivalStatus status)
    {
        var json = JsonSerializer.SerializeToElement(state, Json);
        var projection = json.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        projection["basis"] = JsonSerializer.SerializeToElement(state.Basis.Select(basis => new
        {
            basis.Version,
            basis.ContentDigest,
            basis.BasisDigest,
            basis.CreatedAt,
        }), Json);
        projection["audit"] = JsonSerializer.SerializeToElement(state.Audit.Select(audit => ProjectAudit(audit, role)), Json);
        projection["arrival"] = JsonSerializer.SerializeToElement(status, Json);
        return projection;
    }

    private static object ProjectAudit(AuditEvent audit, string role)
    {
        if (audit.Action != ArrivalJourneyService.SourceReadAction)
            return audit;

        ArrivalSourceReadAuditDetail? detail;
        try
        {
            detail = JsonSerializer.Deserialize<ArrivalSourceReadAuditDetail>(audit.Detail, Json);
        }
        catch (JsonException)
        {
            detail = null;
        }

        if (detail is null)
            return new { audit.Sequence, audit.Revision, audit.Action, audit.At, detail = (object?)null };

        var safeDetail = new Dictionary<string, object?>
        {
            ["sourceId"] = detail.SourceId,
            ["role"] = detail.Role,
            ["outcome"] = detail.Outcome,
        };
        if (detail.ErrorCode is not null) safeDetail["errorCode"] = detail.ErrorCode;
        if (SourceReadEndpoints.Allowed(role, detail.SourceId) && detail.RecordRefs.Count > 0)
            safeDetail["recordRefs"] = detail.RecordRefs;
        return new { audit.Sequence, audit.Revision, audit.Action, audit.At, detail = safeDetail };
    }
}
