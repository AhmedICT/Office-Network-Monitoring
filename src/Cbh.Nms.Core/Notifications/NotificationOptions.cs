namespace Cbh.Nms.Core.Notifications;

public sealed class NotificationOptions
{
    public string TimeZone { get; set; } = "Asia/Dhaka";
    /// <summary>Warning e-mails between these hours go to the morning digest (§9.2).</summary>
    public int NightStartHour { get; set; } = 22;
    public int NightEndHour { get; set; } = 7;
    /// <summary>Repeat SMS for an unacknowledged Critical/High alert at most this often; 0 disables repeats.</summary>
    public int RepeatMinutes { get; set; } = 30;
    public int SmsDailyLimit { get; set; } = 150;
    public decimal SmsMonthlyBudgetBdt { get; set; } = 2000;
    /// <summary>Dashboard link included in messages.</summary>
    public string PublicBaseUrl { get; set; } = "https://nms.pdcl.local/";
}

public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "cbh-nms@pdcl.local";
    /// <summary>Development: write .eml files here instead of sending.</summary>
    public string? PickupDirectory { get; set; }
    public bool Enabled => !string.IsNullOrEmpty(Host) || !string.IsNullOrEmpty(PickupDirectory);
}

/// <summary>
/// Generic HTTP SMS gateway (INT-03). Local BD gateways differ, so the request is a template:
/// {to}, {message} and {apiKey} are substituted (URL-encoded in the URL, JSON-escaped in a JSON body).
/// </summary>
public sealed class SmsOptions
{
    public string? Url { get; set; }
    public string Method { get; set; } = "POST";
    public string? BodyTemplate { get; set; }
    public string ContentType { get; set; } = "application/json";
    public string? ApiKey { get; set; }
    /// <summary>Response must contain this text to count as accepted (empty = any 2xx).</summary>
    public string? SuccessContains { get; set; }
    public decimal CostPerSmsBdt { get; set; } = 0.35m;
    /// <summary>Unicode (Bangla) SMS segments are 70 chars; cost multiplies per segment.</summary>
    public bool Enabled => !string.IsNullOrEmpty(Url);
}
