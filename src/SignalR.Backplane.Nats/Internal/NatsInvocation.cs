using Microsoft.AspNetCore.SignalR;

namespace SignalR.Backplane.Nats.Internal;

internal readonly struct NatsInvocation
{
    /// <summary>
    /// Gets a list of connections that should be excluded from this invocation.
    /// May be null to indicate that no connections are to be excluded.
    /// </summary>
    public IReadOnlyList<string>? ExcludedConnectionIds { get; }

    /// <summary>
    /// Gets the message serialization cache containing serialized payloads for the message.
    /// </summary>
    public SerializedHubMessage Message { get; }

    public string? ReturnChannel { get; }

    public string? InvocationId { get; }

    public NatsInvocation(SerializedHubMessage message, IReadOnlyList<string>? excludedConnectionIds,
        string? invocationId = null, string? returnChannel = null)
    {
        Message = message;
        ExcludedConnectionIds = excludedConnectionIds;
        ReturnChannel = returnChannel;
        InvocationId = invocationId;
    }
}