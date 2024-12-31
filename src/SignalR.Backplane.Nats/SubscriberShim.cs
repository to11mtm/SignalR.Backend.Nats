using System.Collections.Concurrent;
using NATS.Client.Core;

namespace SignalR.Backplane.Nats;


public class SubscriberShim
{
    public ConcurrentDictionary<string, INatsSub<byte[]>> Subscriptions { get; } = new();
    public INatsConnection Connection;

    public SubscriberShim(INatsConnection natsServerConnection)
    {
        Connection = natsServerConnection;
    }

    public async ValueTask UnsubscribeAsync(string subject)
    {
        if (Subscriptions.TryRemove(subject, out var sub))
        {
            await sub.UnsubscribeAsync();
        }
    }

    public async ValueTask SubscribeAsync(string channelsAll, Func<NatsMsg<byte[]>,ValueTask> action)
    {
        if (Subscriptions.TryGetValue(channelsAll, out var sub))
        {
            //???
        }
        else
        {
            var newSub = await Connection.SubscribeCoreAsync<byte[]>(channelsAll);
            Subscriptions[channelsAll] = newSub;
            Task.Run(async () =>
            {
                await foreach (var msg in newSub.Msgs.ReadAllAsync())
                {
                    await action(msg);
                }
            });
        }
    }

    public async ValueTask UnsubscribeAll()
    {
        var cleanAll = Subscriptions.Values.ToArray();
        foreach (var sub in cleanAll)
        {
            _ = sub.UnsubscribeAsync();
        }
    }
}