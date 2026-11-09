using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Notifications;

/// <summary>"Today" in Asia/Dhaka as UTC instants (NFR-14: store UTC, show local).</summary>
public sealed class NmsDbClock(IOptions<NotificationOptions> opt, TimeProvider clock)
{
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(opt.Value.TimeZone);

    public (DateTimeOffset StartUtc, DateTimeOffset NowUtc) TodayUtc()
    {
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        return (new DateTimeOffset(local.Date, local.Offset).ToUniversalTime(), now);
    }
}
