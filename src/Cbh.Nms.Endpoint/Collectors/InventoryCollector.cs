using System.Globalization;
using System.Management;
using Cbh.Nms.Shared;
using Microsoft.Win32;

namespace Cbh.Nms.Endpoint.Collectors;

/// <summary>Hardware (WMI), installed software (registry uninstall keys) and security posture.</summary>
public sealed class InventoryCollector(ILogger<InventoryCollector> log)
{
    public EndpointInventory Collect()
    {
        var hw = Hardware(out var isWorkstation);
        return new EndpointInventory(DateTimeOffset.UtcNow, hw, Security(isWorkstation), Software());
    }

    public static string MachineGuid()
    {
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return k?.GetValue("MachineGuid") as string ?? Environment.MachineName;
    }

    // ───────── hardware ─────────

    private HardwareInfo Hardware(out bool isWorkstation)
    {
        var cs = First("SELECT Manufacturer, Model, Domain, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var os = First("SELECT Caption, Version, BuildNumber, LastBootUpTime, ProductType FROM Win32_OperatingSystem");
        var bios = First("SELECT SerialNumber FROM Win32_BIOS");
        var cpus = All("SELECT Name, NumberOfCores FROM Win32_Processor");
        isWorkstation = Convert.ToInt32(os?["ProductType"] ?? 1) == 1;

        DateTimeOffset? boot = null;
        if (os?["LastBootUpTime"] is string b)
            try { boot = ManagementDateTimeConverter.ToDateTime(b).ToUniversalTime(); } catch (ArgumentException) { }

        return new HardwareInfo(
            Environment.MachineName, cs?["Domain"] as string, (os?["Caption"] as string)?.Trim(), os?["Version"] as string,
            os?["BuildNumber"] as string, (cs?["Manufacturer"] as string)?.Trim(), (cs?["Model"] as string)?.Trim(),
            (bios?["SerialNumber"] as string)?.Trim(), (cpus.FirstOrDefault()?["Name"] as string)?.Trim(),
            cpus.Count == 0 ? null : cpus.Sum(c => Convert.ToInt32(c["NumberOfCores"] ?? 0)),
            cs?["TotalPhysicalMemory"] is { } mem ? (long)(Convert.ToUInt64(mem) / 1048576) : null,
            boot);
    }

    // ───────── security ─────────

    private SecurityInfo Security(bool isWorkstation)
    {
        var av = new List<AntivirusInfo>();
        if (isWorkstation)   // Security Center (root\SecurityCenter2) exists only on client Windows
            foreach (var p in All("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"))
            {
                var (enabled, upToDate) = AvState.Decode(Convert.ToUInt32(p["productState"] ?? 0));
                av.Add(new AntivirusInfo(p["displayName"] as string ?? "?", enabled, upToDate));
            }

        DateTimeOffset? lastUpdate = null;
        foreach (var q in All("SELECT InstalledOn FROM Win32_QuickFixEngineering"))
            if (q["InstalledOn"] is string s && DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out var d)
                && (lastUpdate is null || d > lastUpdate))
                lastUpdate = d;

        return new SecurityInfo(isWorkstation, av,
            Firewall("DomainProfile"), Firewall("StandardProfile"), Firewall("PublicProfile"),
            lastUpdate, PendingReboot());
    }

    /// <summary>Group Policy setting wins over the local one.</summary>
    private static bool? Firewall(string profile)
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var policy = lm.OpenSubKey($@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{profile}");
        if (policy?.GetValue("EnableFirewall") is int p) return p != 0;
        using var local = lm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}");
        return local?.GetValue("EnableFirewall") is int v ? v != 0 : null;
    }

    private static bool PendingReboot()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cbs = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wu = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return cbs is not null || wu is not null;
    }

    // ───────── software ─────────

    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static List<SoftwareItem> Software()
    {
        var items = new List<SoftwareItem>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            Read(lm.OpenSubKey(Uninstall), items);
        }
        // Per-user installs (e.g. Chrome, Zoom, Teams in the user profile) of the users currently logged on.
        using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            foreach (var sid in users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !s.EndsWith("_Classes")))
                Read(users.OpenSubKey($@"{sid}\{Uninstall}"), items);

        return items.DistinctBy(s => (s.Name.ToLowerInvariant(), (s.Version ?? "").ToLowerInvariant())).ToList();
    }

    private static void Read(RegistryKey? root, List<SoftwareItem> into)
    {
        if (root is null) return;
        using (root)
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
                // Windows updates and components are not "software" for this purpose.
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") is string || k.GetValue("ReleaseType") is string rt && rt.Contains("Update")) continue;
                DateOnly? installed = k.GetValue("InstallDate") is string d
                    && DateOnly.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
                static string? Clean(object? v) => (v as string)?.Replace("\0", "").Trim();
                into.Add(new SoftwareItem(Clean(display)!, Clean(k.GetValue("DisplayVersion")), Clean(k.GetValue("Publisher")), installed));
            }
    }

    // ───────── WMI helpers ─────────

    private ManagementBaseObject? First(string query, string scope = @"root\cimv2") => All(query, scope).FirstOrDefault();

    private List<ManagementBaseObject> All(string query, string scope = @"root\cimv2")
    {
        try
        {
            using var s = new ManagementObjectSearcher(scope, query);
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "WMI query failed: {Query}", query);
            return [];
        }
    }
}

/// <summary>Decodes Security Center's AntiVirusProduct.productState (0xXXYYZZ: YY = scanner on/off, ZZ = signatures).</summary>
public static class AvState
{
    public static (bool Enabled, bool UpToDate) Decode(uint state)
    {
        var scanner = (state >> 8) & 0xFF;
        var signatures = state & 0xFF;
        return (scanner is 0x10 or 0x11, signatures == 0x00);
    }
}
