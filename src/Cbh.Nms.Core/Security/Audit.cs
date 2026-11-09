using System.Text.Json;
using Cbh.Nms.Core.Data;

namespace Cbh.Nms.Core;

/// <summary>Adds an audit row to the current unit of work (FR-US-03); saved with the change it describes.</summary>
public static class Audit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Record(this NmsDb db, HttpContext http, string action, string entityType, object? entityId,
        object? oldValue = null, object? newValue = null)
    {
        db.AuditLog.Add(new AuditLog
        {
            UserId = http.User.UserId(),
            UserName = http.User.Identity?.Name ?? "anonymous",
            ClientIp = http.Connection.RemoteIpAddress,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue, Json),
            NewValue = newValue is null ? null : JsonSerializer.Serialize(newValue, Json),
        });
    }
}
