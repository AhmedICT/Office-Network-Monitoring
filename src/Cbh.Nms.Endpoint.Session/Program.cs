using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Endpoint.Session;

/// <summary>
/// CBH-NMS session helper (D-14). Started at logon in each user's session (scheduled task). Every 5 s it notes
/// which program is in the foreground and whether there was keyboard/mouse input recently; once a minute it
/// sends the per-program seconds and the session state to the Endpoint Agent service over a local pipe.
/// It records only the program's file name and product name — never window titles, URLs, keystrokes or
/// screen contents. When HO has activity tracking switched off it does not sample at all.
/// </summary>
internal static partial class Program
{
    private static readonly TimeSpan Sample = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Flush = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ActiveInput = TimeSpan.FromSeconds(60);   // counted as "in use" only with recent input
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string?> ProductCache = new(StringComparer.OrdinalIgnoreCase);

    private static void Main()
    {
        // One helper per session.
        using var single = new Mutex(true, $@"Local\cbhnms-session-{Process.GetCurrentProcess().SessionId}", out var first);
        if (!first) return;

        var track = false;
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", SessionPipe.Name, PipeDirection.InOut, PipeOptions.None,
                    TokenImpersonationLevel.Identification);   // lets the service read who we are from the token
                pipe.Connect(5000);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                var usage = new Dictionary<string, SessionAppUsage>(StringComparer.OrdinalIgnoreCase);
                var state = SessionStates.Active;
                var next = DateTime.UtcNow;   // first message straight away (the "logon")
                while (true)
                {
                    if (DateTime.UtcNow >= next)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(new SessionHelperMessage(state, usage), Json));
                        var reply = reader.ReadLine() is { } line ? JsonSerializer.Deserialize<SessionHelperReply>(line, Json) : null;
                        if (reply is null) break;
                        track = reply.Track;
                        usage = new(StringComparer.OrdinalIgnoreCase);
                        next = DateTime.UtcNow + Flush;
                    }
                    Thread.Sleep(Sample);
                    if (!track) continue;

                    var idle = IdleTime();
                    state = IsLocked() ? SessionStates.Locked : idle >= IdleAfter ? SessionStates.Idle : SessionStates.Active;
                    if (state != SessionStates.Active || idle >= ActiveInput) continue;
                    if (ForegroundApp() is { } app)
                    {
                        var cur = usage.GetValueOrDefault(app.Exe);
                        usage[app.Exe] = new SessionAppUsage((cur?.Seconds ?? 0) + (int)Sample.TotalSeconds, app.Product);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or UnauthorizedAccessException) { }
            Thread.Sleep(TimeSpan.FromSeconds(30));   // service not running yet / restarted: try again
        }
    }

    private static (string Exe, string? Product)? ForegroundApp()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var exe = p.ProcessName + ".exe";
            if (!ProductCache.TryGetValue(exe, out var product))
            {
                try { product = p.MainModule?.FileVersionInfo.ProductName?.Trim(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { product = null; }
                ProductCache[exe] = string.IsNullOrEmpty(product) ? null : product;
            }
            return (exe, ProductCache[exe]);
        }
        catch (ArgumentException) { return null; }   // process already gone
    }

    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    /// <summary>The secure desktop (lock screen / UAC) cannot be opened from a user process.</summary>
    private static bool IsLocked()
    {
        var desk = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
        if (desk == 0) return true;
        var ok = SwitchDesktop(desk);
        CloseDesktop(desk);
        return !ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetLastInputInfo(ref LastInputInfo info);
    [LibraryImport("user32.dll")] private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SwitchDesktop(nint desk);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseDesktop(nint desk);
}
