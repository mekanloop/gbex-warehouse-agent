using Gbex.Warehouse.Agent.Core.Models;

namespace Gbex.Warehouse.Agent.Core.Abstractions;

public enum EasyCubeConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>
/// The PRIMARY EasyCube integration: a persistent raw TCP/IP socket to the
/// device (Ethernet, Protocol 0 — see EasyCubeProtocolZeroParser), which
/// pushes a combined barcode+measurement record every time its own
/// USB-attached barcode scanner reads a package. This is a completely
/// separate abstraction from IEasyCubeClient (the HTTP Web API client) —
/// that one remains only as the optional manual/keyboard-wedge fallback,
/// never the default path. Nothing outside the Infrastructure implementation
/// opens its own socket to the device.
/// </summary>
public interface IEasyCubeConnection
{
    EasyCubeConnectionState State { get; }
    event Action<EasyCubeConnectionState>? StateChanged;

    /// <summary>
    /// Raised once per successfully parsed measurement record — after unit
    /// conversion to KG/CM, so subscribers receive the same CapturedMeasurement
    /// shape the HTTP fallback path produces. Awaited by the raising code, so
    /// a slow subscriber (e.g. submitting to GBEX) naturally back-pressures
    /// further reads rather than racing ahead of processing.
    /// </summary>
    event Func<CapturedMeasurement, CancellationToken, Task>? MeasurementReceived;
}
