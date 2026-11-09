namespace Cbh.Nms.Agent;

public sealed class AgentOptions
{
    /// <summary>Core Platform base URL over MPLS, e.g. https://nms.pdcl.local/</summary>
    public string HoBaseUrl { get; set; } = "";
    /// <summary>One-time token from SCR-04; only read on first start, when no credential exists yet.</summary>
    public string? EnrollToken { get; set; }
    public string DataDirectory { get; set; } = @"C:\ProgramData\CBH-NMS\Agent";
    public TimeSpan SendInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConfigInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan BufferRetention { get; set; } = TimeSpan.FromHours(72);
    /// <summary>~72 h of a busy branch (≈130 metrics/min) with headroom.</summary>
    public int BufferMaxRows { get; set; } = 1_000_000;
    public int SnmpTimeoutMs { get; set; } = 5000;
}
