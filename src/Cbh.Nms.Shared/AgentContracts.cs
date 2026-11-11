namespace Cbh.Nms.Shared;

// Wire contracts for /agent/v1 (SRS §7.2, §7.3). Changing a contract here must stay
// backward compatible: agents in the field run older builds for up to a year.

public static class EntityTypes
{
    public const string Branch = "BRANCH";
    public const string Device = "DEVICE";
    public const string IspLink = "ISPLINK";
    public const string App = "APP";
    public const string Endpoint = "ENDPOINT";   // user PC (D-14)
}

public static class MetricKeys
{
    public const string LatencyMs = "latency_ms";
    public const string JitterMs = "jitter_ms";
    public const string LossPct = "loss_pct";
    public const string Up = "up";                       // 1 = reachable / check passed
    public const string CpuPct = "cpu_pct";
    public const string MemPct = "mem_pct";
    public const string UptimeS = "uptime_s";
    public const string ResponseMs = "response_ms";
    public const string HttpStatus = "http_status";
    public const string UpsOnBattery = "ups_on_battery";
    public const string UpsBatteryPct = "ups_battery_pct";
    public const string UpsInputV = "ups_input_v";
    public const string UpsRuntimeMin = "ups_runtime_min";
}

public sealed record RegisterRequest(string EnrollToken, string HostName, string AgentVersion);
public sealed record RegisterResponse(int AgentId, string Key, int BranchId);

public sealed record MetricItem(long Seq, DateTimeOffset Time, string Entity, int Id, string Key, double Value);
public sealed record MetricBatch(int AgentId, string BatchId, IReadOnlyList<MetricItem> Items);
public sealed record MetricAck(long AckedUpToSeq);

public sealed record AgentEvent(long Seq, DateTimeOffset Time, string Entity, int Id, string Kind, string? Detail);
public sealed record EventBatch(int AgentId, IReadOnlyList<AgentEvent> Items);

public sealed record Heartbeat(string AgentVersion, int ConfigVersion, int BufferRows, double CpuPct, double RamMb);

public sealed record AgentConfig(
    int BranchId,
    int ConfigVersion,
    int PollIntervalSeconds,
    string? HoProbeTarget,
    IReadOnlyList<IspLinkProbe> IspLinks,
    IReadOnlyList<DeviceTarget> Devices,
    IReadOnlyList<AppCheckTarget> AppChecks);

public sealed record IspLinkProbe(int IspLinkId, string Role, string ProbeTarget);

public sealed record DeviceTarget(
    int DeviceId,
    string DeviceType,
    string? Vendor,
    string IpAddress,
    int PollIntervalSeconds,
    SnmpCredential? Snmp,
    IReadOnlyDictionary<string, string> Oids);

// Secrets travel only inside the TLS channel to an authenticated agent; the agent keeps them in memory.
public sealed record SnmpCredential(string User, string AuthProtocol, string AuthSecret, string PrivProtocol, string PrivSecret);

public sealed record AppCheckTarget(int AppCheckId, string Type, string Target, int? ExpectedStatus, int TimeoutMs);

public static class AgentLimits
{
    public const int MaxBatchRows = 5000;
}
