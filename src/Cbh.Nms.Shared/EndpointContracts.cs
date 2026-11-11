namespace Cbh.Nms.Shared;

// Wire contracts for /endpoint/v1 — the Endpoint Agent on user PCs (D-14). Backward compatible like /agent/v1.

public static class EndpointMetricKeys
{
    public const string CpuPct = "cpu_pct";
    public const string MemPct = "mem_pct";
    public const string DiskFreePct = "disk_free_pct";     // system drive
    public const string GatewayRttMs = "gw_rtt_ms";
    public const string GatewayLossPct = "gw_loss_pct";
    public const string HoRttMs = "ho_rtt_ms";
    public const string HoLossPct = "ho_loss_pct";
    public const string RxKbps = "rx_kbps";
    public const string TxKbps = "tx_kbps";
}

public sealed record EndpointRegisterRequest(string EnrollKey, string HostName, string MachineGuid, string? AgentVersion);
public sealed record EndpointRegisterResponse(int EndpointId, string Key, int BranchId);

/// <param name="ActivityTracking">When false the agent must not collect application usage or session events.</param>
public sealed record EndpointConfig(int ReportIntervalS, int InventoryIntervalMin, bool ActivityTracking, string? ProbeTarget);

/// <summary>Every upload carries a BatchId so a re-send after a lost response is ignored, not double-counted.</summary>
public sealed record EndpointUpload<T>(Guid BatchId, T Body);

public sealed record EndpointReport(DateTimeOffset Time, string? LoggedOnUser, EndpointHealth Health, EndpointNetwork Network);

public sealed record EndpointHealth(
    double CpuPct, double MemPct, long MemTotalMb, long UptimeS,
    IReadOnlyList<DiskInfo> Disks, IReadOnlyList<ProcessUsage> TopProcesses);

public sealed record DiskInfo(string Name, double TotalGb, double FreeGb, bool IsSystem);

public sealed record ProcessUsage(string Name, double CpuPct, double MemMb, int Connections);

public sealed record EndpointNetwork(
    string? PrimaryIp, string? Mac, string? Gateway,
    double? GatewayRttMs, double? GatewayLossPct, double? HoRttMs, double? HoLossPct,
    double RxKbps, double TxKbps, IReadOnlyList<NicInfo> Nics);

public sealed record NicInfo(string Name, string Type, string? Ip, string? Mac, long SpeedMbps, bool Up);

public sealed record EndpointInventory(
    DateTimeOffset Time, HardwareInfo Hardware, SecurityInfo Security, IReadOnlyList<SoftwareItem> Software);

public sealed record HardwareInfo(
    string HostName, string? Domain, string? OsName, string? OsVersion, string? OsBuild, string? Manufacturer,
    string? Model, string? SerialNo, string? CpuModel, int? CpuCores, long? RamMb, DateTimeOffset? LastBootAt);

/// <param name="AntivirusSupported">False on Windows Server, which has no Security Center to ask.</param>
public sealed record SecurityInfo(
    bool AntivirusSupported, IReadOnlyList<AntivirusInfo> Antivirus,
    bool? FirewallDomain, bool? FirewallPrivate, bool? FirewallPublic,
    DateTimeOffset? LastUpdateInstalled, bool PendingReboot);

public sealed record AntivirusInfo(string Name, bool Enabled, bool UpToDate);

public sealed record SoftwareItem(string Name, string? Version, string? Publisher, DateOnly? InstallDate);

public sealed record ActivityBatch(IReadOnlyList<AppUsageItem> Usage, IReadOnlyList<SessionEventItem> Events);

/// <summary>Seconds the app was in the foreground with recent keyboard/mouse input. No titles or URLs.</summary>
public sealed record AppUsageItem(DateOnly Day, string User, string App, string? Product, int ActiveSeconds);

public sealed record SessionEventItem(DateTimeOffset Time, string User, string Event);

public static class SessionEvents
{
    public const string Logon = "LOGON";
    public const string Logoff = "LOGOFF";
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string Idle = "IDLE";
    public const string Active = "ACTIVE";
}

// ───────── Session helper ⇄ Endpoint Agent service (local named pipe, one JSON line each way per minute) ─────────

public static class SessionPipe
{
    public const string Name = "cbhnms-endpoint-session";
    public const int MaxAppsPerMessage = 200;
}

public static class SessionStates
{
    public const string Active = "ACTIVE";
    public const string Idle = "IDLE";       // no keyboard/mouse input for 5 minutes
    public const string Locked = "LOCKED";
}

/// <param name="Apps">exe name → foreground seconds with recent input since the previous message.</param>
public sealed record SessionHelperMessage(string State, Dictionary<string, SessionAppUsage> Apps);
public sealed record SessionAppUsage(int Seconds, string? Product);
/// <param name="Track">False when activity tracking is off by policy: the helper then stops sampling.</param>
public sealed record SessionHelperReply(bool Track);
