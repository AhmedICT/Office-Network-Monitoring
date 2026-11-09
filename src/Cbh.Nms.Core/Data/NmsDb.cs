using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Data;

public class NmsDb(DbContextOptions<NmsDb> options) : DbContext(options)
{
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<BranchContact> BranchContacts => Set<BranchContact>();
    public DbSet<Isp> Isps => Set<Isp>();
    public DbSet<IspLink> IspLinks => Set<IspLink>();
    public DbSet<DeviceType> DeviceTypes => Set<DeviceType>();
    public DbSet<SnmpProfile> SnmpProfiles => Set<SnmpProfile>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceDependency> DeviceDependencies => Set<DeviceDependency>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<EnrollToken> EnrollTokens => Set<EnrollToken>();
    public DbSet<AppCheck> AppChecks => Set<AppCheck>();
    public DbSet<BranchConfigVersion> BranchConfigVersions => Set<BranchConfigVersion>();
    public DbSet<StatusCurrent> StatusCurrent => Set<StatusCurrent>();
    public DbSet<StatusHistory> StatusHistory => Set<StatusHistory>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserBranchScope> UserBranchScopes => Set<UserBranchScope>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Schema is owned by db/migrations/*.sql; this mapping must mirror it.
        b.Entity<Region>().ToTable("region", "org");
        b.Entity<Branch>().ToTable("branch", "org");
        b.Entity<Branch>().Property(x => x.CreatedAt).ValueGeneratedOnAdd();
        b.Entity<BranchContact>().ToTable("branch_contact", "org");
        b.Entity<Isp>().ToTable("isp", "org");
        b.Entity<IspLink>().ToTable("isp_link", "org");

        b.Entity<DeviceType>().ToTable("device_type", "inv").Property(x => x.DeviceTypeId).ValueGeneratedNever();
        b.Entity<SnmpProfile>().ToTable("snmp_profile", "inv").Property(x => x.Oids).HasColumnType("jsonb");
        b.Entity<Device>().ToTable("device", "inv");
        b.Entity<DeviceDependency>().ToTable("device_dependency", "inv").HasKey(x => new { x.ChildDeviceId, x.ParentDeviceId });
        b.Entity<Agent>().ToTable("agent", "inv");
        b.Entity<Agent>().Property(x => x.RegisteredAt).ValueGeneratedOnAdd();
        b.Entity<EnrollToken>().ToTable("enroll_token", "inv").HasKey(x => x.TokenHash);
        b.Entity<AppCheck>().ToTable("app_check", "inv");
        b.Entity<BranchConfigVersion>().ToTable("branch_config_version", "inv").HasKey(x => x.BranchId);
        b.Entity<BranchConfigVersion>().Property(x => x.BranchId).ValueGeneratedNever();

        b.Entity<StatusCurrent>().ToTable("status_current", "mon").HasKey(x => new { x.EntityType, x.EntityId });
        b.Entity<StatusCurrent>().Property(x => x.Detail).HasColumnType("jsonb");
        b.Entity<StatusHistory>().ToTable("status_history", "mon");
        b.Entity<MaintenanceWindow>().ToTable("maintenance_window", "mon").HasKey(x => x.MaintenanceId);

        b.Entity<AppUser>().ToTable("app_user", "sec").HasKey(x => x.UserId);
        b.Entity<UserBranchScope>().ToTable("user_branch_scope", "sec").HasKey(x => x.ScopeId);
        b.Entity<AuditLog>().ToTable("audit_log", "sec").HasKey(x => x.AuditId);
        b.Entity<AuditLog>().Property(x => x.At).ValueGeneratedOnAdd();
        b.Entity<AuditLog>().Property(x => x.OldValue).HasColumnType("jsonb");
        b.Entity<AuditLog>().Property(x => x.NewValue).HasColumnType("jsonb");
    }
}
