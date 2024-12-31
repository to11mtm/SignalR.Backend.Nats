using System.Buffers;

namespace SignalR.Backplane.Nats.Internal;

internal readonly struct NatsCompletion
{
    public ReadOnlySequence<byte> CompletionMessage { get; }

    public string ProtocolName { get; }

    public NatsCompletion(string protocolName, ReadOnlySequence<byte> completionMessage)
    {
        ProtocolName = protocolName;
        CompletionMessage = completionMessage;
    }
}