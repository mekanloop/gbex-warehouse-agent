using System.Text;
using Gbex.Warehouse.Agent.Core.Abstractions;

namespace Gbex.Warehouse.Agent.Infrastructure.Diagnostics;

public static class DiagnosticsReportBuilder
{
    public static async Task<DiagnosticsReport> BuildAsync(
        string agentVersion,
        string gbexApiBaseUrl,
        string gbexConnectionState,
        DateTimeOffset? lastHeartbeatAt,
        ISecretStore secretStore,
        string easyCubeBaseUrl,
        string easyCubeConnectionState,
        string? easyCubeDeviceModel,
        string? easyCubeSoftwareVersion,
        string? deviceId,
        IOutboxStore outbox,
        CancellationToken ct)
    {
        return new DiagnosticsReport
        {
            AgentVersion = agentVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            GbexApiBaseUrl = gbexApiBaseUrl,
            GbexConnectionState = gbexConnectionState,
            LastSuccessfulHeartbeatAtUtc = lastHeartbeatAt,
            StationSecretConfigured = await secretStore.HasStationSecretAsync(ct),
            EasyCubeBaseUrl = easyCubeBaseUrl,
            EasyCubeConnectionState = easyCubeConnectionState,
            EasyCubeDeviceModel = easyCubeDeviceModel,
            EasyCubeSoftwareVersion = easyCubeSoftwareVersion,
            DeviceId = deviceId,
            OfflineQueueCount = await outbox.CountPendingAsync(ct),
            RequiresReauthorizationCount = await outbox.CountByStateAsync(OutboxItemState.RequiresReauthorization, ct),
            RequiresManualResolutionCount = await outbox.CountByStateAsync(OutboxItemState.RequiresManualResolution, ct),
            RecentSanitizedErrors = await outbox.GetRecentSanitizedErrorsAsync(10, ct),
        };
    }

    /// <summary>Plain-text rendering — a non-technical operator emails/attaches this file as-is; a support engineer reads it as-is. No JSON/XML ceremony needed for either audience.</summary>
    public static string RenderAsText(DiagnosticsReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GBEX Depo Ajanı — Tanılama Raporu");
        sb.AppendLine($"Oluşturulma zamanı (UTC): {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Ajan sürümü: {report.AgentVersion}");
        sb.AppendLine();
        sb.AppendLine("== GBEX Bağlantısı ==");
        sb.AppendLine($"Adres: {report.GbexApiBaseUrl}");
        sb.AppendLine($"Durum: {report.GbexConnectionState}");
        sb.AppendLine($"Son başarılı nabız: {(report.LastSuccessfulHeartbeatAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "hiç yok")}");
        sb.AppendLine($"İstasyon anahtarı kayıtlı mı: {(report.StationSecretConfigured ? "Evet" : "Hayır")}");
        sb.AppendLine();
        sb.AppendLine("== EasyCube Bağlantısı ==");
        sb.AppendLine($"Adres: {report.EasyCubeBaseUrl}");
        sb.AppendLine($"Durum: {report.EasyCubeConnectionState}");
        sb.AppendLine($"Cihaz modeli: {report.EasyCubeDeviceModel ?? "bilinmiyor"}");
        sb.AppendLine($"Yazılım sürümü: {report.EasyCubeSoftwareVersion ?? "bilinmiyor"}");
        sb.AppendLine($"Cihaz kimliği (ayarlarda girilen): {report.DeviceId ?? "girilmemiş"}");
        sb.AppendLine();
        sb.AppendLine("== Çevrimdışı Kuyruk ==");
        sb.AppendLine($"Bekleyen işlem sayısı: {report.OfflineQueueCount}");
        sb.AppendLine($"Yeniden yetkilendirme gereken: {report.RequiresReauthorizationCount}");
        sb.AppendLine($"Elle çözüm gereken: {report.RequiresManualResolutionCount}");
        sb.AppendLine();
        sb.AppendLine("== Son Hatalar ==");
        if (report.RecentSanitizedErrors.Count == 0)
        {
            sb.AppendLine("(Kayıtlı hata yok)");
        }
        else
        {
            foreach (var error in report.RecentSanitizedErrors)
            {
                sb.AppendLine($"- {error}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Bu rapor istasyon anahtarını, müşteri bilgilerini veya kargo/taşıyıcı verilerini içermez.");
        return sb.ToString();
    }
}
