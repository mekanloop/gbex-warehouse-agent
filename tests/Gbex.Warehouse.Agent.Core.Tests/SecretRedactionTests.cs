using System.Net;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Gbex.Warehouse.Agent.Infrastructure.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

/// <summary>Captures every log message produced during a real (fake-transport) HTTP call and asserts the station secret never appears in any of them.</summary>
public class SecretRedactionTests
{
    private const string Secret = "wst_super_secret_value_should_never_be_logged";

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<string> _messages;
            public CapturingLogger(List<string> messages) => _messages = messages;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _messages.Add(formatter(state, exception));
                if (exception is not null) _messages.Add(exception.ToString());
            }
            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Simulate a failure so the client's error-logging path runs too.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"message\":\"boom\"}"),
            });
        }
    }

    [Fact]
    public async Task GbexApiClient_never_logs_the_authorization_header_or_secret_value()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));

        var secretStore = new InMemorySecretStore();
        await secretStore.SaveStationSecretAsync(Secret, CancellationToken.None);

        var httpClient = new HttpClient(new FakeHandler());
        var options = Options.Create(new GbexApiOptions { BaseUrl = "https://example.invalid" });
        var client = new GbexApiClient(httpClient, options, secretStore, loggerFactory.CreateLogger<GbexApiClient>());

        await client.HeartbeatAsync("1.0.0", CancellationToken.None);
        await client.LookupOrderAsync("GBEX2508230001", CancellationToken.None);

        foreach (var message in loggerProvider.Messages)
        {
            Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InMemorySecretStore_round_trips_and_removes_cleanly()
    {
        var store = new InMemorySecretStore();
        Assert.False(await store.HasStationSecretAsync(CancellationToken.None));

        await store.SaveStationSecretAsync(Secret, CancellationToken.None);
        Assert.True(await store.HasStationSecretAsync(CancellationToken.None));
        Assert.Equal(Secret, await store.TryGetStationSecretAsync(CancellationToken.None));

        await store.RemoveStationSecretAsync(CancellationToken.None);
        Assert.False(await store.HasStationSecretAsync(CancellationToken.None));
        Assert.Null(await store.TryGetStationSecretAsync(CancellationToken.None));
    }
}
