using Gbex.Warehouse.Agent.Core.Abstractions;

namespace Gbex.Warehouse.Agent.Windows.Printing;

/// <summary>Phase 6's only ILabelPrinter implementation — a fake. Never prints anything real; exists purely so the interface/DI wiring is exercised end-to-end. A real printer implementation is out of scope for this phase.</summary>
public sealed class FakeLabelPrinter : ILabelPrinter
{
    public Task<PrinterStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new PrinterStatus { Status = PrinterCapabilityStatus.NotConfigured, Detail = "No printer configured in Phase 6." });

    public Task<bool> PrintAsync(byte[] labelBytes, string mimeType, CancellationToken ct) =>
        Task.FromResult(false);
}
