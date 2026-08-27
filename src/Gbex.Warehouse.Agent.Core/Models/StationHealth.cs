namespace Gbex.Warehouse.Agent.Core.Models;

/// <summary>Connectivity state for the GBEX backend link, surfaced by the heartbeat service.</summary>
public enum StationConnectionState
{
    Connected,
    Degraded,
    Offline,
    Unauthorized,
    Disabled,
}

/// <summary>The operator-visible workflow state — one enum, matches the required WPF UI states exactly.</summary>
public enum AgentWorkflowState
{
    Ready,
    LookingUpOrder,
    OrderFound,
    Measuring,
    Submitting,
    VerifiedPass,
    OnHoldMismatch,
    OfflineQueued,
    StationUnauthorized,
    StationDisabled,
    EasyCubeError,
}
