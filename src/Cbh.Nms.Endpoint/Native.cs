using System.Runtime.InteropServices;

namespace Cbh.Nms.Endpoint;

/// <summary>The few Win32 calls .NET has no managed API for.</summary>
internal static partial class Native
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int af, int tableClass, uint reserved);

    /// <summary>Established IPv4 TCP connections per owning process id.</summary>
    public static Dictionary<int, int> TcpConnectionsByPid()
    {
        var result = new Dictionary<int, int>();
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size == 0) return result;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0) return result;
            var count = Marshal.ReadInt32(buf);
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid — six DWORDs.
            for (var i = 0; i < count; i++)
            {
                var row = buf + 4 + i * 24;
                var state = Marshal.ReadInt32(row);
                if (state != 5) continue;                      // MIB_TCP_STATE_ESTAB
                var pid = Marshal.ReadInt32(row + 20);
                result[pid] = result.GetValueOrDefault(pid) + 1;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }
}
