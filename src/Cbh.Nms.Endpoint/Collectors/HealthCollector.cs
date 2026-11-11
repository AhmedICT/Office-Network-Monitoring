using System.Diagnostics;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Endpoint.Collectors;

/// <summary>CPU, memory, disks and the processes using the most CPU / memory / connections.
/// CPU figures are averages since the previous call (one report interval).</summary>
public sealed class HealthCollector
{
    private const int TopN = 10;
    private (long Idle, long Kernel, long User)? _lastSys;
    private Dictionary<int, TimeSpan> _lastCpu = [];
    private DateTime _lastSample = DateTime.UtcNow;

    public EndpointHealth Collect()
    {
        var cpu = SystemCpu();

        var mem = new Native.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryStatusEx>() };
        Native.GlobalMemoryStatusEx(ref mem);
        var memTotalMb = (long)(mem.TotalPhys / 1048576);
        var memPct = mem.TotalPhys == 0 ? 0 : 100.0 * (mem.TotalPhys - mem.AvailPhys) / mem.TotalPhys;

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var disks = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskInfo(d.Name, Math.Round(d.TotalSize / 1073741824.0, 1), Math.Round(d.AvailableFreeSpace / 1073741824.0, 1),
                string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new EndpointHealth(Math.Round(cpu, 1), Math.Round(memPct, 1), memTotalMb, Environment.TickCount64 / 1000, disks, TopProcesses());
    }

    private double SystemCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var prev = _lastSys;
        _lastSys = (idle, kernel, user);
        if (prev is not { } p) return 0;
        var total = (kernel - p.Kernel) + (user - p.User);     // kernel time includes idle time
        return total <= 0 ? 0 : Math.Clamp(100.0 * (total - (idle - p.Idle)) / total, 0, 100);
    }

    private List<ProcessUsage> TopProcesses()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSample).TotalMilliseconds * Environment.ProcessorCount;
        _lastSample = now;
        var conns = Native.TcpConnectionsByPid();
        var cpuNow = new Dictionary<int, TimeSpan>();
        // Summed per process name, so 30 browser processes show as one line.
        var byName = new Dictionary<string, (double Cpu, double Mem, int Conns)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue;                  // Idle, System
                double cpu = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    cpuNow[p.Id] = t;
                    if (_lastCpu.TryGetValue(p.Id, out var before) && elapsed > 0)
                        cpu = 100.0 * (t - before).TotalMilliseconds / elapsed;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                double memMb;
                try { memMb = p.WorkingSet64 / 1048576.0; }
                catch (InvalidOperationException) { continue; }
                var name = p.ProcessName + ".exe";
                var cur = byName.GetValueOrDefault(name);
                byName[name] = (cur.Cpu + cpu, cur.Mem + memMb, cur.Conns + conns.GetValueOrDefault(p.Id));
            }
        }
        _lastCpu = cpuNow;

        var all = byName.Select(kv => new ProcessUsage(kv.Key, Math.Round(Math.Clamp(kv.Value.Cpu, 0, 100), 1),
            Math.Round(kv.Value.Mem, 0), kv.Value.Conns)).ToList();
        return all.OrderByDescending(x => x.CpuPct).Take(TopN)
            .Concat(all.OrderByDescending(x => x.MemMb).Take(TopN))
            .Concat(all.Where(x => x.Connections > 0).OrderByDescending(x => x.Connections).Take(TopN))
            .DistinctBy(x => x.Name)
            .OrderByDescending(x => x.CpuPct).ThenByDescending(x => x.MemMb)
            .ToList();
    }
}
