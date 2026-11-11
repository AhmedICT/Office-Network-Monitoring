using Cbh.Nms.Core.Endpoints;
using Cbh.Nms.Endpoint.Activity;
using Cbh.Nms.Endpoint.Collectors;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Tests;

public class SoftwareDiffTests
{
    private static List<InstalledSoftware> L(params (string N, string V)[] x) => x.Select(i => new InstalledSoftware(i.N, i.V)).ToList();

    [Fact]
    public void First_inventory_is_a_baseline_without_events() =>
        Assert.Empty(SoftwareDiff.Compute([], L(("Chrome", "130"), ("7-Zip", "24"))));

    [Fact]
    public void Detects_installed_removed_and_updated()
    {
        var changes = SoftwareDiff.Compute(L(("Chrome", "130"), ("Zoom", "6.0")), L(("Chrome", "131"), ("AnyDesk", "9")));
        Assert.Contains(new SoftwareChange("UPDATED", "Chrome", "131", "130"), changes);
        Assert.Contains(new SoftwareChange("INSTALLED", "AnyDesk", "9", null), changes);
        Assert.Contains(new SoftwareChange("REMOVED", "Zoom", "6.0", null), changes);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void Names_compare_case_insensitively() =>
        Assert.Empty(SoftwareDiff.Compute(L(("Google Chrome", "130")), L(("google chrome", "130"))));

    [Fact]
    public void Second_version_side_by_side_is_an_install_not_an_update()
    {
        var changes = SoftwareDiff.Compute(L(("Java", "8")), L(("Java", "8"), ("Java", "21")));
        Assert.Equal([new SoftwareChange("INSTALLED", "Java", "21", null)], changes);
    }

    [Theory]
    [InlineData("*torrent*", "%torrent%")]
    [InlineData("AnyDesk", "AnyDesk")]
    [InlineData("100%_free?", "100\\%\\_free_")]
    public void Wildcards_become_escaped_like_patterns(string pattern, string like) => Assert.Equal(like, Wildcard.ToLike(pattern));
}

public class EndpointSecurityTests
{
    [Theory]
    [InlineData(0x061100u, true, true)]    // Defender on, up to date
    [InlineData(0x061110u, true, false)]   // on, signatures out of date
    [InlineData(0x060100u, false, true)]   // off
    [InlineData(0x041000u, true, true)]
    public void Decodes_security_center_product_state(uint state, bool enabled, bool upToDate) =>
        Assert.Equal((enabled, upToDate), AvState.Decode(state));

    private static SecurityInfo Sec(params AntivirusInfo[] av) => new(true, av, true, true, true, null, false);

    [Fact]
    public void Healthy_pc_has_no_findings() =>
        Assert.Empty(EndpointAlertSource.SecurityFindings(Sec(new AntivirusInfo("Defender", true, true))));

    [Fact]
    public void Missing_or_disabled_antivirus_is_high()
    {
        Assert.Equal(("av", Severity.High, "No antivirus"), EndpointAlertSource.SecurityFindings(Sec()).Single());
        Assert.Equal("Antivirus disabled", EndpointAlertSource.SecurityFindings(Sec(new AntivirusInfo("X", false, true))).Single().Text);
    }

    [Fact]
    public void Out_of_date_antivirus_is_warning() =>
        Assert.Equal(("av-old", Severity.Warning), EndpointAlertSource.SecurityFindings(Sec(new AntivirusInfo("X", true, false)))
            .Select(f => (f.Kind, f.Severity)).Single());

    [Fact]
    public void Server_without_security_center_is_not_flagged_for_antivirus() =>
        Assert.Empty(EndpointAlertSource.SecurityFindings(new SecurityInfo(false, [], true, true, true, null, false)));

    [Fact]
    public void Firewall_off_lists_the_profiles()
    {
        var f = EndpointAlertSource.SecurityFindings(Sec(new AntivirusInfo("D", true, true)) with { FirewallPublic = false, FirewallPrivate = false }).Single();
        Assert.Equal("Windows Firewall off (private, public)", f.Text);
    }
}

public class ActivityAggregatorTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);
    private static SessionHelperMessage M(string state, params (string App, int S)[] apps) =>
        new(state, apps.ToDictionary(a => a.App, a => new SessionAppUsage(a.S, a.App == "chrome.exe" ? "Google Chrome" : null)));

    [Fact]
    public void Sums_seconds_per_user_and_app_and_records_logon()
    {
        var a = new ActivityAggregator();
        a.Connected(@"PDCL\rahim", T);
        a.Report(@"PDCL\rahim", M(SessionStates.Active, ("chrome.exe", 40), ("his.exe", 20)), T);
        a.Report(@"PDCL\rahim", M(SessionStates.Active, ("Chrome.exe", 30)), T.AddMinutes(1));
        var b = a.Drain()!;
        Assert.Equal(70, b.Usage.Single(u => u.App == "chrome.exe").ActiveSeconds);
        Assert.Equal("Google Chrome", b.Usage.Single(u => u.App == "chrome.exe").Product);
        Assert.Equal([SessionEvents.Logon], b.Events.Select(e => e.Event));
        Assert.Null(a.Drain());
    }

    [Fact]
    public void Caps_implausible_seconds_per_message()
    {
        var a = new ActivityAggregator();
        a.Report("u", M(SessionStates.Active, ("x.exe", 100_000)), T);
        Assert.Equal(ActivityAggregator.MaxSecondsPerMessage, a.Drain()!.Usage.Single().ActiveSeconds);
    }

    [Fact]
    public void State_changes_become_session_events()
    {
        var a = new ActivityAggregator();
        a.Connected("u", T);
        a.Report("u", M(SessionStates.Idle), T.AddMinutes(1));
        a.Report("u", M(SessionStates.Locked), T.AddMinutes(2));
        a.Report("u", M(SessionStates.Active), T.AddMinutes(3));
        a.Disconnected("u", T.AddMinutes(4));
        Assert.Equal([SessionEvents.Logon, SessionEvents.Idle, SessionEvents.Lock, SessionEvents.Unlock, SessionEvents.Logoff],
            a.Drain()!.Events.Select(e => e.Event));
    }

    [Fact]
    public void Second_session_of_same_user_does_not_log_off_on_first_disconnect()
    {
        var a = new ActivityAggregator();
        a.Connected("u", T); a.Connected("u", T);
        a.Disconnected("u", T.AddMinutes(1));
        Assert.Equal([SessionEvents.Logon], a.Drain()!.Events.Select(e => e.Event));
    }

    [Fact]
    public void Clear_discards_everything()
    {
        var a = new ActivityAggregator();
        a.Report("u", M(SessionStates.Active, ("x.exe", 5)), T);
        a.Clear();
        Assert.Null(a.Drain());
    }
}
