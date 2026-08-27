using System.Net.Http;

namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

/// <summary>
/// EasyCube's own HTTP Web API can be configured for HTTPS
/// (`/websconfig`'s `HttpsInUse`) — confirmed against a real device that
/// responds "Client sent an HTTP request to an HTTPS server" on its Web API
/// port. An embedded device like this has no real CA-issued certificate; it
/// is self-signed by construction. This handler trusts whatever certificate
/// the configured EasyCube host presents, which is an acceptable trade-off
/// for a device on the depot's own private LAN that the operator already
/// physically controls (unlike GbexApiClient's connection to the public
/// internet, which must never relax certificate validation — this handler
/// is used ONLY for EasyCube connections, never for GBEX).
/// </summary>
public static class EasyCubeHttpClientFactory
{
    public static HttpClientHandler CreateHandler() => new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    };
}
