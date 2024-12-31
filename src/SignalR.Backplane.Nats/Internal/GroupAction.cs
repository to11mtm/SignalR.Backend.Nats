namespace SignalR.Backplane.Nats.Internal;

internal enum GroupAction : byte
{
    // These numbers are used by the protocol, do not change them and always use explicit assignment
    // when adding new items to this enum. 0 is intentionally omitted
    Add = 1,
    Remove = 2,
}