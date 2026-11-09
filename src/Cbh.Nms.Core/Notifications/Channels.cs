using System.Net;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Cbh.Nms.Core.Notifications;

public interface IEmailSender
{
    bool Enabled { get; }
    Task SendAsync(string to, string subject, string body, IReadOnlyList<(string Name, byte[] Data, string MimeType)>? attachments, CancellationToken ct);
}

public interface ISmsGateway
{
    bool Enabled { get; }
    Task SendAsync(string to, string message, CancellationToken ct);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailOptions _opt = options.Value;
    public bool Enabled => _opt.Enabled;

    public async Task SendAsync(string to, string subject, string body,
        IReadOnlyList<(string Name, byte[] Data, string MimeType)>? attachments, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_opt.From));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        var builder = new BodyBuilder { TextBody = body };
        foreach (var (name, data, mime) in attachments ?? [])
            builder.Attachments.Add(name, data, ContentType.Parse(mime));
        msg.Body = builder.ToMessageBody();

        if (!string.IsNullOrEmpty(_opt.PickupDirectory))
        {
            Directory.CreateDirectory(_opt.PickupDirectory);
            await msg.WriteToAsync(Path.Combine(_opt.PickupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.eml"), ct);
            return;
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_opt.Host!, _opt.Port, _opt.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(_opt.User)) await smtp.AuthenticateAsync(_opt.User, _opt.Password ?? "", ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

public sealed class HttpSmsGateway(HttpClient http, IOptions<SmsOptions> options, ILogger<HttpSmsGateway> log) : ISmsGateway
{
    private readonly SmsOptions _opt = options.Value;
    public bool Enabled => _opt.Enabled;

    public async Task SendAsync(string to, string message, CancellationToken ct)
    {
        var url = _opt.Url!
            .Replace("{to}", WebUtility.UrlEncode(to))
            .Replace("{message}", WebUtility.UrlEncode(message))
            .Replace("{apiKey}", WebUtility.UrlEncode(_opt.ApiKey ?? ""));
        using var req = new HttpRequestMessage(new HttpMethod(_opt.Method), url);
        if (_opt.BodyTemplate is { Length: > 0 } tpl)
        {
            var json = _opt.ContentType.Contains("json");
            string Esc(string s) => json ? JsonEncodedText.Encode(s).ToString() : WebUtility.UrlEncode(s);
            var body = tpl.Replace("{to}", Esc(to)).Replace("{message}", Esc(message)).Replace("{apiKey}", Esc(_opt.ApiKey ?? ""));
            req.Content = new StringContent(body, Encoding.UTF8, _opt.ContentType);
        }
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode || (_opt.SuccessContains is { Length: > 0 } ok && !text.Contains(ok)))
            throw new InvalidOperationException($"SMS gateway rejected: {(int)res.StatusCode} {Truncate(text)}");
        log.LogInformation("SMS sent to {To}", to);
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;

    /// <summary>GSM-7 fits 160 chars per SMS; Unicode (Bangla) only 70. Multipart segments are 153/67.</summary>
    public static int Segments(string message)
    {
        var unicode = message.Any(c => c > 127);
        var (single, multi) = unicode ? (70, 67) : (160, 153);
        return message.Length <= single ? 1 : (int)Math.Ceiling(message.Length / (double)multi);
    }
}
