using NATS.Client.Core;

namespace SignalR.Backplane.Nats;

/// <summary>
/// Options used to configure <see cref="NatsHubLifetimeManager{THub}"/>.
/// </summary>
public class SignalRNatsOptions
{
    /// <summary>
    /// Gets or sets configuration options exposed by <c>StackExchange.Nats</c>.
    /// </summary>
    public NatsOpts Configuration { get; set; } = new NatsOpts()
        with
        { 
            // NATS has reconnect by default
        };

    /// <summary>
    /// Gets or sets the Nats connection factory.
    /// </summary>
    public Func<TextWriter, Task<INatsConnection>>? ConnectionFactory { get; set; }

    internal async Task<INatsConnection> ConnectAsync(TextWriter log)
    {
        // Factory is publicly settable. Assigning to a local variable before null check for thread safety.
        var factory = ConnectionFactory;
        if (factory == null)
        {
            var cnn = new NatsConnection(Configuration);
            await cnn.ConnectAsync();
            return cnn;
            // suffix SignalR onto the declared library name
            // var provider = DefaultOptionsProvider.GetProvider(Configuration.EndPoints);
            // Configuration.LibraryName = $"{provider.LibraryName} SignalR";
            // return await ConnectionMultiplexer.ConnectAsync(Configuration, log);
        }

        return await factory(log);
    }
}