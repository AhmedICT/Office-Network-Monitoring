using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Endpoint.Activity;

/// <summary>
/// Local named-pipe server for the session helpers (one per logged-on user). The user name is taken from the
/// pipe client's Windows token, not from the message, so one user cannot report as another. The pipe refuses
/// network clients. Each message is answered with whether tracking is on, so helpers idle when it is off.
/// </summary>
public sealed class ActivityHub(ActivityAggregator aggregator, ILogger<ActivityHub> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Track { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(SessionPipe.Name, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    4096, 4096, Security());
                await server.WaitForConnectionAsync(ct);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleAsync(connected, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Session pipe error");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            finally { server?.Dispose(); }
        }
    }

    private static PipeSecurity Security()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The service's own account must be able to add pipe instances (SYSTEM when installed; the developer in a dev run).
        using (var me = WindowsIdentity.GetCurrent())
            s.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }

    /// <summary>DOMAIN\user of the connected helper, from its own token (Identification-level impersonation).</summary>
    private static string ClientAccount(NamedPipeServerStream pipe)
    {
        string? name = null;
        pipe.RunAsClient(() => { using var id = WindowsIdentity.GetCurrent(); name = id.Name; });
        return name ?? pipe.GetImpersonationUserName();
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? user = null;
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (user is null)
                    {
                        user = ClientAccount(pipe);
                        aggregator.Connected(user, DateTimeOffset.UtcNow);
                    }
                    if (line.Length <= 64_000 && JsonSerializer.Deserialize<SessionHelperMessage>(line, Json) is { } msg && Track)
                        aggregator.Report(user, msg with { Apps = msg.Apps ?? [] }, DateTimeOffset.UtcNow);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new SessionHelperReply(Track), Json).AsMemory(), ct);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or InvalidOperationException) { }
            finally
            {
                if (user is not null) aggregator.Disconnected(user, DateTimeOffset.UtcNow);
            }
        }
    }
}
