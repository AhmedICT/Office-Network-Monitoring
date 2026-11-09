namespace Cbh.Nms.Core.Data;

public class AlertRule
{
    public int AlertRuleId { get; set; }
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public string Operator { get; set; } = ">";
    public double Threshold { get; set; }
    public int ForSeconds { get; set; }
    public string Severity { get; set; } = "WARNING";
    public int? ScopeBranchId { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class Alert
{
    public long AlertId { get; set; }
    public string DedupKey { get; set; } = "";
    public int? AlertRuleId { get; set; }
    public int BranchId { get; set; }
    public int? DeviceId { get; set; }
    public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public double? LastValue { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public bool IsSuppressed { get; set; }
    public string? SuppressReason { get; set; }
    public int? AckByUserId { get; set; }
    public DateTimeOffset? AckAt { get; set; }
    public string? AckComment { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
}

public class Incident
{
    public long IncidentId { get; set; }
    public string IncidentNo { get; set; } = "";
    public int BranchId { get; set; }
    public string Severity { get; set; } = "";
    public string Status { get; set; } = IncidentStatus.Open;
    public string Title { get; set; } = "";
    public string? RootCause { get; set; }
    public string? ResolutionNote { get; set; }
    public int? AssignedUserId { get; set; }
    public short EscalationLevel { get; set; }
    public string? TaskTicketId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset? AckAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public short TicketAttempts { get; set; }
    public DateTimeOffset? TicketNextAttemptAt { get; set; }
    public string? TicketStatus { get; set; }
    public uint Version { get; set; }   // xmin concurrency token (SRS §6.2)
}

public static class IncidentStatus
{
    public const string Open = "OPEN";
    public const string Ack = "ACK";
    public const string InProgress = "IN_PROGRESS";
    public const string Resolved = "RESOLVED";
    public const string Closed = "CLOSED";
    public static readonly string[] All = [Open, Ack, InProgress, Resolved, Closed];
    public static readonly string[] Active = [Open, Ack, InProgress];
}

public static class RootCauses
{
    public static readonly string[] All = ["ISP", "POWER", "HARDWARE", "CONFIG", "HIS", "UNKNOWN"];
}

public class IncidentAlert
{
    public long IncidentId { get; set; }
    public long AlertId { get; set; }
}

public class IncidentTimeline
{
    public long TimelineId { get; set; }
    public long IncidentId { get; set; }
    public DateTimeOffset At { get; set; }
    public int? UserId { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

public class EscalationPolicy
{
    public int EscalationPolicyId { get; set; }
    public string Severity { get; set; } = "";
    public short Level { get; set; }
    public int AfterMinutes { get; set; }
    public string TargetRole { get; set; } = "";
    public string Channels { get; set; } = "";
}

public class NotificationRule
{
    public int NotificationRuleId { get; set; }
    public int? BranchId { get; set; }
    public string MinSeverity { get; set; } = "";
    public string TimeWindow { get; set; } = "ANY";
    public int UserId { get; set; }
    public string Channels { get; set; } = "";
}

public class NotificationLog
{
    public long NotificationId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Channel { get; set; } = "";
    public string Recipient { get; set; } = "";
    public int? UserId { get; set; }
    public long? AlertId { get; set; }
    public long? IncidentId { get; set; }
    public string Status { get; set; } = "QUEUED";
    public decimal CostBdt { get; set; }
    public string? Error { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public short Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public bool IsDigest { get; set; }
}
