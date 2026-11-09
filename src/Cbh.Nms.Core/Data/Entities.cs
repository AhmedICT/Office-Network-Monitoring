using System.Net;
using System.Net.NetworkInformation;

namespace Cbh.Nms.Core.Data;

// EF entities for the master/config tables. Time-series tables (mon.metric_raw,
// mon.reachability) are written with raw Npgsql COPY and never go through EF.

public class Region
{
    public int RegionId { get; set; }
    public string Name { get; set; } = "";
}

public class Branch
{
    public int BranchId { get; set; }
    public string BranchCode { get; set; } = "";
    public string BranchName { get; set; } = "";
    public int RegionId { get; set; }
    public Region? Region { get; set; }
    public string? Address { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public short Tier { get; set; } = 2;
    public TimeOnly? OpenTime { get; set; }
    public TimeOnly? CloseTime { get; set; }
    public IPAddress? MplsRouterIp { get; set; }
    public decimal SlaTargetPct { get; set; } = 99.50m;
    public decimal LossThresholdPct { get; set; } = 5;
    public int LatencyThresholdMs { get; set; } = 300;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<IspLink> IspLinks { get; set; } = [];
    public List<BranchContact> Contacts { get; set; } = [];
}

public class BranchContact
{
    public int BranchContactId { get; set; }
    public int BranchId { get; set; }
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsPrimary { get; set; }
}

public class Isp
{
    public int IspId { get; set; }
    public string Name { get; set; } = "";
    public string? SupportPhone { get; set; }
    public string? SupportEmail { get; set; }
}

public class IspLink
{
    public int IspLinkId { get; set; }
    public int BranchId { get; set; }
    public int IspId { get; set; }
    public Isp? Isp { get; set; }
    public string LinkRole { get; set; } = "PRIMARY";
    public string? CircuitId { get; set; }
    public int BandwidthMbps { get; set; }
    public string? WanInterface { get; set; }
    public IPAddress? ProbeTarget { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DeviceType
{
    public short DeviceTypeId { get; set; }
    public string Name { get; set; } = "";
}

public class SnmpProfile
{
    public int SnmpProfileId { get; set; }
    public string Name { get; set; } = "";
    public string? Vendor { get; set; }
    public string SnmpUser { get; set; } = "";
    public string AuthProtocol { get; set; } = "SHA";
    public string PrivProtocol { get; set; } = "AES";
    public string AuthSecretEnc { get; set; } = "";
    public string PrivSecretEnc { get; set; } = "";
    public string Oids { get; set; } = "{}";      // jsonb: metric_key -> OID
}

public class Device
{
    public int DeviceId { get; set; }
    public int BranchId { get; set; }
    public short DeviceTypeId { get; set; }
    public DeviceType? DeviceType { get; set; }
    public string? Vendor { get; set; }
    public string HostName { get; set; } = "";
    public IPAddress IpAddress { get; set; } = IPAddress.None;
    public PhysicalAddress? MacAddress { get; set; }
    public string? Model { get; set; }
    public string? SerialNo { get; set; }
    public string? Firmware { get; set; }
    public DateOnly? WarrantyEnd { get; set; }
    public int? SnmpProfileId { get; set; }
    public SnmpProfile? SnmpProfile { get; set; }
    public short PollIntervalS { get; set; } = 60;
    public bool IsCritical { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DeviceDependency
{
    public int ChildDeviceId { get; set; }
    public int ParentDeviceId { get; set; }
}

public class Agent
{
    public int AgentId { get; set; }
    public int BranchId { get; set; }
    public string HostName { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public string? AgentVersion { get; set; }
    public int ConfigVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public int? BufferRows { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public bool IsRevoked { get; set; }
}

public class EnrollToken
{
    public byte[] TokenHash { get; set; } = [];
    public int BranchId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class AppCheck
{
    public int AppCheckId { get; set; }
    public int? BranchId { get; set; }
    public string Name { get; set; } = "";
    public string CheckType { get; set; } = "HTTP";
    public string Target { get; set; } = "";
    public short? ExpectedStatus { get; set; }
    public int TimeoutMs { get; set; } = 5000;
    public string? BusinessService { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BranchConfigVersion
{
    public int BranchId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

public class StatusCurrent
{
    public string EntityType { get; set; } = "";
    public int EntityId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class StatusHistory
{
    public long StatusHistoryId { get; set; }
    public string EntityType { get; set; } = "";
    public int EntityId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool InMaintenance { get; set; }
}

public class MaintenanceWindow
{
    public int MaintenanceId { get; set; }
    public int? BranchId { get; set; }
    public int? DeviceId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Reason { get; set; } = "";
    public string? ChangeRequestNo { get; set; }
    public int CreatedBy { get; set; }
    public int? ApprovedBy { get; set; }
}

public class AppUser
{
    public int UserId { get; set; }
    public string? AdSid { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string Role { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public class UserBranchScope
{
    public int ScopeId { get; set; }
    public int UserId { get; set; }
    public int? RegionId { get; set; }
    public int? BranchId { get; set; }
}

public class AuditLog
{
    public long AuditId { get; set; }
    public DateTimeOffset At { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public IPAddress? ClientIp { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
