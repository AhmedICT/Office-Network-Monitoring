using Cbh.Nms.Agent;
using Cbh.Nms.Agent.Collectors;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "CBH-NMS Agent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

builder.Services.AddSingleton(sp =>
    new MetricBuffer(Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory, "buffer.db")));
builder.Services.AddSingleton(sp =>
    new CredentialStore(Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory, "agent.key")));
builder.Services.AddSingleton(sp =>
    new ConfigCache(Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory, "config.bin")));

builder.Services.AddHttpClient<HoClient>((sp, c) =>
{
    var url = sp.GetRequiredService<IOptions<AgentOptions>>().Value.HoBaseUrl;
    if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Agent:HoBaseUrl is not set");
    c.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
// HIS checks must not follow redirects silently or reuse HO's auth header.
builder.Services.AddHttpClient<AppProbe>(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddHostedService<AgentService>();

builder.Build().Run();
