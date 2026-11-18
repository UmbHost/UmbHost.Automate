using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UmbHost.Automate.Teams.Actions;
using UmbHost.Automate.Teams.Connection;
using UmbHost.Automate.Teams.Http;

namespace UmbHost.Automate.Teams.Tests;

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

    public RecordingHandler(HttpStatusCode status = HttpStatusCode.Accepted, string body = "")
        : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body) })
    {
    }

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

    public List<(Uri? Uri, byte[] Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
        return _reply(request);
    }
}

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<string> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recorder(this);

    public void Dispose()
    {
    }

    private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
            }
        }
    }
}

internal static class PostCardTestSupport
{
    internal const string Address = "https://prod-12.westeurope.logic.azure.com:443/workflows/abc123/triggers/manual/paths/invoke?sig=SECRETSIG";

    internal static readonly TeamsConnectionSettings Connection = new() { WebhookUrl = Address };

    internal static PostCardAction Sut(RecordingHandler handler, RecordingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });
        TeamsWebhookClient.Register(services);
        services.AddHttpClient(TeamsWebhookClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return new PostCardAction(ActionTestHarness.Infrastructure, services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }
}
