namespace Cbh.Nms.Shared;

public static class BranchStatus
{
    public const string Online = "ONLINE";
    public const string Degraded = "DEGRADED";
    public const string OfflineIsp = "OFFLINE_ISP";
    public const string OfflinePower = "OFFLINE_POWER";
    public const string Unknown = "UNKNOWN";
}

public static class Severity
{
    public const string Critical = "CRITICAL";
    public const string High = "HIGH";
    public const string Warning = "WARNING";
    public const string Info = "INFO";
}

public static class Roles
{
    public const string SuperAdmin = "SUPER_ADMIN";
    public const string HeadOfIt = "HEAD_OF_IT";
    public const string ItManager = "IT_MANAGER";
    public const string NetAdmin = "NET_ADMIN";
    public const string BranchEngineer = "BRANCH_ENGINEER";
    public const string Management = "MANAGEMENT";
}
