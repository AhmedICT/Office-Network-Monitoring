using System.Diagnostics;
using System.Net.Sockets;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Agent.Collectors;

public sealed record AppResult(bool Up, double ResponseMs, int? HttpStatus);

/// <summary>HIS/LIS health checks as seen from the branch (FR-AP-01, FR-AP-02). Service state only — no patient data.</summary>
public sealed class AppProbe(HttpClient http)
{
    public async Task<AppResult> RunAsync(AppCheckTarget check, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(check.TimeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            if (check.Type == "TCP")
            {
                var (host, port) = SplitHostPort(check.Target);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, cts.Token);
                return new AppResult(true, sw.Elapsed.TotalMilliseconds, null);
            }

            using var res = await http.GetAsync(check.Target, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)res.StatusCode;
            var ok = check.ExpectedStatus is { } exp ? code == exp : res.IsSuccessStatusCode;
            return new AppResult(ok, sw.Elapsed.TotalMilliseconds, code);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new AppResult(false, sw.Elapsed.TotalMilliseconds, null);
        }
    }

    internal static (string Host, int Port) SplitHostPort(string target)
    {
        var i = target.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(target[(i + 1)..], out var port))
            throw new FormatException($"TCP check target must be host:port, got '{target}'");
        return (target[..i], port);
    }
}
