using PenBridge.Input;

namespace PenBridge.Models;

public sealed record MonitorOption(string Id, string Label, MonitorRect Bounds);
public sealed record ClientDetails(string Address, string UserAgent, DateTimeOffset ConnectedAt,
    long Samples, DateTimeOffset? LastInput, bool Streaming);
