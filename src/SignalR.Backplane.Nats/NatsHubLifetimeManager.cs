using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SignalR.Backplane.Nats.Internal;

namespace SignalR.Backplane.Nats;

/// <summary>
/// The Nats scaleout provider for multi-server support.
/// </summary>
/// <typeparam name="THub">The type of <see cref="Hub"/> to manage connections for.</typeparam>
public class NatsHubLifetimeManager<THub> : HubLifetimeManager<THub>, IDisposable where THub : Hub
{
    private readonly HubConnectionStore _connections = new HubConnectionStore();
    private readonly NatsSubscriptionManager _groups = new NatsSubscriptionManager();
    private readonly NatsSubscriptionManager _users = new NatsSubscriptionManager();
    
    private INatsConnection? _NatsServerConnection;
    private SubscriberShim? _bus;
    private readonly ILogger _logger;
    private readonly SignalRNatsOptions _options;
    private readonly NatsChannels _channels;
    private readonly string _serverName = GenerateServerName();
    private readonly NatsProtocol _protocol;
    private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1);
    private readonly IHubProtocolResolver _hubProtocolResolver;
    private readonly ClientResultsManager _clientResultsManager = new();
    private bool _NatsConnectErrorLogged;

    private readonly AckHandler _ackHandler;
    private int _internalAckId;

    /// <summary>
    /// Constructs the <see cref="NatsHubLifetimeManager{THub}"/> with types from Dependency Injection.
    /// </summary>
    /// <param name="logger">The logger to write information about what the class is doing.</param>
    /// <param name="options">The <see cref="NatsOptions"/> that influence behavior of the Nats connection.</param>
    /// <param name="hubProtocolResolver">The <see cref="IHubProtocolResolver"/> to get an <see cref="IHubProtocol"/> instance when writing to connections.</param>
    public NatsHubLifetimeManager(ILogger<NatsHubLifetimeManager<THub>> logger,
        IOptions<SignalRNatsOptions> options,
        IHubProtocolResolver hubProtocolResolver)
        : this(logger, options, hubProtocolResolver, globalHubOptions: null, hubOptions: null)
    {
    }

    /// <summary>
    /// Constructs the <see cref="NatsHubLifetimeManager{THub}"/> with types from Dependency Injection.
    /// </summary>
    /// <param name="logger">The logger to write information about what the class is doing.</param>
    /// <param name="options">The <see cref="NatsOptions"/> that influence behavior of the Nats connection.</param>
    /// <param name="hubProtocolResolver">The <see cref="IHubProtocolResolver"/> to get an <see cref="IHubProtocol"/> instance when writing to connections.</param>
    /// <param name="globalHubOptions">The global <see cref="HubOptions"/>.</param>
    /// <param name="hubOptions">The <typeparamref name="THub"/> specific options.</param>
    public NatsHubLifetimeManager(ILogger<NatsHubLifetimeManager<THub>> logger,
        IOptions<SignalRNatsOptions> options,
        IHubProtocolResolver hubProtocolResolver,
        IOptions<HubOptions>? globalHubOptions,
        IOptions<HubOptions<THub>>? hubOptions)
    {
        _hubProtocolResolver = hubProtocolResolver;
        _logger = logger;
        _options = options.Value;
        _ackHandler = new AckHandler();
        _channels = new NatsChannels(typeof(THub).FullName!, _serverName);
        if (globalHubOptions != null && hubOptions != null)
        {
            _protocol = new NatsProtocol(new DefaultHubMessageSerializer(hubProtocolResolver, globalHubOptions.Value.SupportedProtocols, hubOptions.Value.SupportedProtocols));
        }
        else
        {
            var supportedProtocols = hubProtocolResolver.AllProtocols.Select(p => p.Name).ToList();
            _protocol = new NatsProtocol(new DefaultHubMessageSerializer(hubProtocolResolver, supportedProtocols, null));
        }

        NatsLog.ConnectingToEndpoints(_logger, [options.Value.Configuration.Url], _serverName);
        _ = EnsureNatsServerConnection();
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        await EnsureNatsServerConnection();

        var feature = new NatsFeature();
        connection.Features.Set<INatsFeature>(feature);

        var userTask = Task.CompletedTask;

        _connections.Add(connection);

        var connectionTask = SubscribeToConnection(connection);

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            userTask = SubscribeToUser(connection);
        }

        await Task.WhenAll(connectionTask, userTask);
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);

        // If the bus is null then the Nats connection failed to be established and none of the other connection setup ran
        if (_bus is null)
        {
            return Task.CompletedTask;
        }

        var connectionChannel = _channels.Connection(connection.ConnectionId);
        var tasks = new List<Task>();

        NatsLog.Unsubscribe(_logger, connectionChannel);
        tasks.Add(_bus.UnsubscribeAsync(connectionChannel).AsTask());

        var feature = connection.Features.GetRequiredFeature<INatsFeature>();
        var groupNames = feature.Groups;

        if (groupNames != null)
        {
            // Copy the groups to an array here because they get removed from this collection
            // in RemoveFromGroupAsync
            foreach (var group in groupNames.ToArray())
            {
                // Use RemoveGroupAsyncCore because the connection is local and we don't want to
                // accidentally go to other servers with our remove request.
                tasks.Add(RemoveGroupAsyncCore(connection, group));
            }
        }

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            tasks.Add(RemoveUserAsync(connection));
        }

        return Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.All, message);
    }

    /// <inheritdoc />
    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        var message = _protocol.WriteInvocation(methodName, args, excludedConnectionIds: excludedConnectionIds);
        return PublishAsync(_channels.All, message);
    }

    /// <inheritdoc />
    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);

        // If the connection is local we can skip sending the message through the bus since we require sticky connections.
        // This also saves serializing and deserializing the message!
        var connection = _connections[connectionId];
        if (connection != null)
        {
            return connection.WriteAsync(new InvocationMessage(methodName, args), cancellationToken).AsTask();
        }

        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.Connection(connectionId), message);
    }

    /// <inheritdoc />
    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupName);

        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.Group(groupName), message);
    }

    /// <inheritdoc />
    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupName);

        var message = _protocol.WriteInvocation(methodName, args, excludedConnectionIds: excludedConnectionIds);
        return PublishAsync(_channels.Group(groupName), message);
    }

    /// <inheritdoc />
    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.User(userId), message);
    }

    /// <inheritdoc />
    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(groupName);

        var connection = _connections[connectionId];
        if (connection != null)
        {
            // short circuit if connection is on this server
            return AddGroupAsyncCore(connection, groupName);
        }

        return SendGroupActionAndWaitForAck(connectionId, groupName, GroupAction.Add);
    }

    /// <inheritdoc />
    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(groupName);

        var connection = _connections[connectionId];
        if (connection != null)
        {
            // short circuit if connection is on this server
            return RemoveGroupAsyncCore(connection, groupName);
        }

        return SendGroupActionAndWaitForAck(connectionId, groupName, GroupAction.Remove);
    }

    /// <inheritdoc />
    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);

        var publishTasks = new List<Task>(connectionIds.Count);
        var payload = _protocol.WriteInvocation(methodName, args);

        foreach (var connectionId in connectionIds)
        {
            publishTasks.Add(PublishAsync(_channels.Connection(connectionId), payload));
        }

        return Task.WhenAll(publishTasks);
    }

    /// <inheritdoc />
    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupNames);
        var publishTasks = new List<Task>(groupNames.Count);
        var payload = _protocol.WriteInvocation(methodName, args);

        foreach (var groupName in groupNames)
        {
            if (!string.IsNullOrEmpty(groupName))
            {
                publishTasks.Add(PublishAsync(_channels.Group(groupName), payload));
            }
        }

        return Task.WhenAll(publishTasks);
    }

    /// <inheritdoc />
    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        if (userIds.Count > 0)
        {
            var payload = _protocol.WriteInvocation(methodName, args);
            var publishTasks = new List<Task>(userIds.Count);
            foreach (var userId in userIds)
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    publishTasks.Add(PublishAsync(_channels.User(userId), payload));
                }
            }

            return Task.WhenAll(publishTasks);
        }

        return Task.CompletedTask;
    }

    private async Task
        //<long>
        PublishAsync(string channel, byte[] payload, bool throwForNoResponders = false)
    {
        await EnsureNatsServerConnection();
        NatsLog.PublishToChannel(_logger, channel);
        await _NatsServerConnection!.PublishAsync(channel, payload);
    }
    
    private async Task<NatsMsg<byte>>
        //<long>
        ReqInvocationAsync(string channel, byte[] payload, bool throwForNoResponders = true)
    {
        await EnsureNatsServerConnection();
        NatsLog.PublishToChannel(_logger, channel);
        return await _NatsServerConnection!.RequestAsync<byte[], byte>(channel, payload,
            replyOpts: throwForNoResponders ? NoResponders : null);
    }

    private static NatsSubOpts NoResponders = new NatsSubOpts() with { ThrowIfNoResponders = true }; 

    private Task AddGroupAsyncCore(HubConnectionContext connection, string groupName)
    {
        var feature = connection.Features.GetRequiredFeature<INatsFeature>();
        var groupNames = feature.Groups;

        lock (groupNames)
        {
            // Connection already in group
            if (!groupNames.Add(groupName))
            {
                return Task.CompletedTask;
            }
        }

        var groupChannel = _channels.Group(groupName);
        return _groups.AddSubscriptionAsync(groupChannel, connection, SubscribeToGroupAsync);
    }

    /// <summary>
    /// This takes <see cref="HubConnectionContext"/> because we want to remove the connection from the
    /// _connections list in OnDisconnectedAsync and still be able to remove groups with this method.
    /// </summary>
    private async Task RemoveGroupAsyncCore(HubConnectionContext connection, string groupName)
    {
        var groupChannel = _channels.Group(groupName);

        await _groups.RemoveSubscriptionAsync(groupChannel, connection, this, static (state, channelName) =>
        {
            var lifetimeManager = (NatsHubLifetimeManager<THub>)state;
            NatsLog.Unsubscribe(lifetimeManager._logger, channelName);
            return lifetimeManager._bus!.UnsubscribeAsync(channelName).AsTask();
        });

        var feature = connection.Features.GetRequiredFeature<INatsFeature>();
        var groupNames = feature.Groups;
        if (groupNames != null)
        {
            lock (groupNames)
            {
                groupNames.Remove(groupName);
            }
        }
    }

    private async Task SendGroupActionAndWaitForAck(string connectionId, string groupName, GroupAction action)
    {
        var id = Interlocked.Increment(ref _internalAckId);
        var ack = _ackHandler.CreateAck(id);
        // Send Add/Remove Group to other servers and wait for an ack or timeout
        var message = NatsProtocol.WriteGroupCommand(new NatsGroupCommand(id, _serverName, action, groupName, connectionId));
        await PublishAsync(_channels.GroupManagement, message);

        await ack;
    }

    private Task RemoveUserAsync(HubConnectionContext connection)
    {
        var userChannel = _channels.User(connection.UserIdentifier!);

        return _users.RemoveSubscriptionAsync(userChannel, connection, this, static (state, channelName) =>
        {
            var lifetimeManager = (NatsHubLifetimeManager<THub>)state;
            NatsLog.Unsubscribe(lifetimeManager._logger, channelName);
            return lifetimeManager._bus!.UnsubscribeAsync(channelName).AsTask();
        });
    }

    /// <summary>
    /// Cleans up the Nats connection.
    /// </summary>
    public void Dispose()
    {
        _bus?.UnsubscribeAll();
        _NatsServerConnection?.DisposeAsync();
        _ackHandler.Dispose();
    }

    /// <inheritdoc/>
    public override async Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken)
    {
        // send thing
        ArgumentNullException.ThrowIfNull(connectionId);

        var connection = _connections[connectionId];

        // ID needs to be unique for each invocation and across servers, we generate a GUID every time, that should provide enough uniqueness guarantees.
        var invocationId = GenerateInvocationId();

        using var _ = CancellationTokenUtils.CreateLinkedToken(cancellationToken,
            connection?.ConnectionAborted ?? default, out var linkedToken);
        var task = _clientResultsManager.AddInvocation<T>(connectionId, invocationId, linkedToken);

        try
        {
            if (connection == null)
            {
                // TODO: Need to handle other server going away while waiting for connection result
                var messageBytes = _protocol.WriteInvocation(methodName, args, invocationId, returnChannel: _channels.ReturnResults);
                // TODO: Fallback for ack.
                await ReqInvocationAsync(_channels.Connection(connectionId), messageBytes, throwForNoResponders: true);
            }
            else
            {
                // We're sending to a single connection
                // Write message directly to connection without caching it in memory
                var message = new InvocationMessage(invocationId, methodName, args);

                await connection.WriteAsync(message, cancellationToken);
            }
        }
        catch
        {
            _clientResultsManager.RemoveInvocation(invocationId);
            throw;
        }

        try
        {
            return await task;
        }
        catch
        {
            // ConnectionAborted will trigger a generic "Canceled" exception from the task, let's convert it into a more specific message.
            if (connection?.ConnectionAborted.IsCancellationRequested == true)
            {
                throw new IOException($"Connection '{connectionId}' disconnected.");
            }
            throw;
        }
    }

    /// <inheritdoc/>
    public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        _clientResultsManager.TryCompleteResult(connectionId, result);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        return _clientResultsManager.TryGetType(invocationId, out type);
    }

    private async Task SubscribeToAll()
    {
        NatsLog.Subscribing(_logger, _channels.All);
        await _bus!.SubscribeAsync(_channels.All, async channelMessage =>
        {
            try
            {
                NatsLog.ReceivedFromChannel(_logger, _channels.All);

                var invocation = NatsProtocol.ReadInvocation(channelMessage.Data);

                var tasks = new List<Task>(_connections.Count);

                foreach (var connection in _connections)
                {
                    if (invocation.ExcludedConnectionIds == null || !invocation.ExcludedConnectionIds.Contains(connection.ConnectionId))
                    {
                        tasks.Add(connection.WriteAsync(invocation.Message).AsTask());
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                NatsLog.FailedWritingMessage(_logger, ex);
            }
        });
    }

    private async Task SubscribeToGroupManagementChannel()
    {
        await _bus!.SubscribeAsync(_channels.GroupManagement,async channelMessage =>
        {
            try
            {
                var groupMessage = NatsProtocol.ReadGroupCommand(channelMessage.Data);

                var connection = _connections[groupMessage.ConnectionId];
                if (connection == null)
                {
                    // user not on this server
                    return;
                }

                if (groupMessage.Action == GroupAction.Remove)
                {
                    await RemoveGroupAsyncCore(connection, groupMessage.GroupName);
                }

                if (groupMessage.Action == GroupAction.Add)
                {
                    await AddGroupAsyncCore(connection, groupMessage.GroupName);
                }

                // Send an ack to the server that sent the original command.
                await PublishAsync(_channels.Ack(groupMessage.ServerName), NatsProtocol.WriteAck(groupMessage.Id));
            }
            catch (Exception ex)
            {
                NatsLog.InternalMessageFailed(_logger, ex);
            }
        });
    }

    private async Task SubscribeToAckChannel()
    {
        // Create server specific channel in order to send an ack to a single server
        await _bus!.SubscribeAsync(_channels.Ack(_serverName),
            async channelMessage =>
            {
                var ackId = NatsProtocol.ReadAck(channelMessage.Data);

                _ackHandler.TriggerAck(ackId);
            });
    }

    private async Task SubscribeToConnection(HubConnectionContext connection)
    {
        var connectionChannel = _channels.Connection(connection.ConnectionId);

        NatsLog.Subscribing(_logger, connectionChannel);
        await _bus!.SubscribeAsync(connectionChannel, 
            channelMessage =>
            {
                var invocation = NatsProtocol.ReadInvocation(channelMessage.Data);

                // This is a Client result we need to setup state for the completion and forward the message to the client
                if (!string.IsNullOrEmpty(invocation.InvocationId))
                {
                    if (channelMessage.ReplyTo != null)
                    {
                        _ = channelMessage.ReplyAsync((byte)1).ConfigureAwait(false);
                    }
                    CancellationTokenRegistration? tokenRegistration = null;
                    _clientResultsManager.AddInvocation(invocation.InvocationId,
                        (typeof(RawResult), connection.ConnectionId, null!, async (_, completionMessage) =>
                        {
                            var protocolName = connection.Protocol.Name;
                            tokenRegistration?.Dispose();

                            var memoryBufferWriter = MemoryBufferWriter.Get();
                            byte[] message;
                            try
                            {
                                try
                                {
                                    connection.Protocol.WriteMessage(completionMessage, memoryBufferWriter);
                                    message = NatsProtocol.WriteCompletionMessage(memoryBufferWriter, protocolName);
                                }
                                finally
                                {
                                    memoryBufferWriter.Dispose();
                                }
                                await PublishAsync(invocation.ReturnChannel!, message);
                            }
                            catch (Exception ex)
                            {
                                NatsLog.ErrorForwardingResult(_logger, completionMessage.InvocationId!, ex);
                            }
                        }));

                    // TODO: this isn't great
                    tokenRegistration = connection.ConnectionAborted.UnsafeRegister(_ =>
                    {
                        var invocationInfo = _clientResultsManager.RemoveInvocation(invocation.InvocationId);
                        invocationInfo?.Completion(null!, CompletionMessage.WithError(invocation.InvocationId, "Connection disconnected."));
                    }, null);
                }

                // Forward message from other server to client
                // Normal client method invokes and client result invokes use the same message
                return connection.WriteAsync(invocation.Message);
            });
    }

    private Task SubscribeToUser(HubConnectionContext connection)
    {
        var userChannel = _channels.User(connection.UserIdentifier!);

        return _users.AddSubscriptionAsync(userChannel, connection, async (channelName, subscriptions) =>
        {
            NatsLog.Subscribing(_logger, channelName);
            await _bus!.SubscribeAsync(channelName,
                async channelMessage =>
                {
                    try
                    {
                        var invocation = NatsProtocol.ReadInvocation(channelMessage.Data);

                        var tasks = new List<Task>(subscriptions.Count);
                        foreach (var userConnection in subscriptions)
                        {
                            tasks.Add(userConnection.WriteAsync(invocation.Message).AsTask());
                        }

                        await Task.WhenAll(tasks);
                    }
                    catch (Exception ex)
                    {
                        NatsLog.FailedWritingMessage(_logger, ex);
                    }
                });
        });
    }

    private async Task SubscribeToGroupAsync(string groupChannel, HubConnectionStore groupConnections)
    {
        NatsLog.Subscribing(_logger, groupChannel);
        await _bus!.SubscribeAsync((groupChannel),
            async (channelMessage) =>
            {
                try
                {
                    var invocation = NatsProtocol.ReadInvocation(channelMessage.Data);

                    var tasks = new List<Task>(groupConnections.Count);
                    foreach (var groupConnection in groupConnections)
                    {
                        if (invocation.ExcludedConnectionIds?.Contains(groupConnection.ConnectionId) == true)
                        {
                            continue;
                        }

                        tasks.Add(groupConnection.WriteAsync(invocation.Message).AsTask());
                    }

                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    NatsLog.FailedWritingMessage(_logger, ex);
                }
            });
    }

    private async Task SubscribeToReturnResultsAsync()
    {
        await _bus!.SubscribeAsync(_channels.ReturnResults,
            async (channelMessage) =>
            {
                var completion = NatsProtocol.ReadCompletion(channelMessage.Data);
                IHubProtocol? protocol = null;
                foreach (var hubProtocol in _hubProtocolResolver.AllProtocols)
                {
                    if (hubProtocol.Name.Equals(completion.ProtocolName))
                    {
                        protocol = hubProtocol;
                        break;
                    }
                }

                // Should only happen if you have different versions of servers and don't have the same protocols registered on both
                if (protocol is null)
                {
                    NatsLog.MismatchedServers(_logger, completion.ProtocolName);
                    return;
                }

                var ros = completion.CompletionMessage;
                HubMessage? hubMessage = null;
                bool retryForError = false;
                try
                {
                    var parseSuccess = protocol.TryParseMessage(ref ros, _clientResultsManager, out hubMessage);
                    retryForError = !parseSuccess;
                }
                catch
                {
                    // Client returned wrong type? Or just an error from the HubProtocol, let's try with RawResult as the type and see if that works
                    retryForError = true;
                }

                if (retryForError)
                {
                    try
                    {
                        ros = completion.CompletionMessage;
                        // if this works then we know there was an error with the type the client returned, we'll replace the CompletionMessage below and provide an error to the application code
                        if (!protocol.TryParseMessage(ref ros, FakeInvocationBinder.Instance, out hubMessage))
                        {
                            NatsLog.ErrorParsingResult(_logger, completion.ProtocolName, null);
                            return;
                        }
                    }
                    // Exceptions here would mean the HubProtocol implementation very likely has a bug, the other server has already deserialized the message (with RawResult) so it should be deserializable
                    // We don't know the InvocationId, we should let the application developer know and potentially surface the issue to the HubProtocol implementor
                    catch (Exception ex)
                    {
                        NatsLog.ErrorParsingResult(_logger, completion.ProtocolName, ex);
                        return;
                    }
                }

                var invocationInfo = _clientResultsManager.RemoveInvocation(((CompletionMessage)hubMessage!).InvocationId!);

                if (retryForError && invocationInfo is not null)
                {
                    hubMessage = CompletionMessage.WithError(((CompletionMessage)hubMessage!).InvocationId!, $"Client result wasn't deserializable to {invocationInfo?.Type.Name}.");
                }

                invocationInfo?.Completion(invocationInfo?.Tcs!, (CompletionMessage)hubMessage!);
            });
    }

    private class FakeInvocationBinder : IInvocationBinder
    {
        public static readonly FakeInvocationBinder Instance = new FakeInvocationBinder();

        private FakeInvocationBinder() { }

        public IReadOnlyList<Type> GetParameterTypes(string methodName)
        {
            throw new NotImplementedException();
        }

        public Type GetReturnType(string invocationId)
        {
            return typeof(RawResult);
        }

        public Type GetStreamItemType(string streamId)
        {
            throw new NotImplementedException();
        }
    }

    private async Task EnsureNatsServerConnection()
    {
        if (_NatsServerConnection == null)
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_NatsServerConnection == null)
                {
                    var writer = new LoggerTextWriter(_logger);
                    try
                    {
                        _NatsServerConnection = await _options.ConnectAsync(writer);
                    }
                    catch (Exception ex)
                    {
                        // If the connection hasn't been established yet we shouldn't keep logging the same error over and over
                        // for every new client connection.
                        if (!_NatsConnectErrorLogged)
                        {
                            NatsLog.ErrorConnecting(_logger, ex);
                            _NatsConnectErrorLogged = true;
                        }
                        throw;
                    }
                    _bus = new SubscriberShim(_NatsServerConnection);
                    _NatsServerConnection.ConnectionOpened += (_, e) =>
                    {
                        NatsLog.ConnectionRestored(_logger);
                        return ValueTask.CompletedTask;
                    };
                    _NatsServerConnection.ConnectionDisconnected += (_, e) =>
                    {
                        NatsLog.ConnectionFailed(_logger, new NatsException(e.Message));
                        return ValueTask.CompletedTask;
                    };
                    /*
                    _NatsServerConnection.ConnectionRestored += (_, e) =>
                    {
                        // We use the subscription connection type
                        // Ignore messages from the interactive connection (avoids duplicates)
                        if (e.ConnectionType == ConnectionType.Interactive)
                        {
                            return;
                        }

                        NatsLog.ConnectionRestored(_logger);
                    };

                    _NatsServerConnection.ConnectionFailed += (_, e) =>
                    {
                        // We use the subscription connection type
                        // Ignore messages from the interactive connection (avoids duplicates)
                        if (e.ConnectionType == ConnectionType.Interactive)
                        {
                            return;
                        }

                        if (e.Exception is not null)
                        {
                            NatsLog.ConnectionFailed(_logger, e.Exception);
                        }
                    };
*/
                    if (_NatsServerConnection.ConnectionState == NatsConnectionState.Open)
                    {
                        NatsLog.Connected(_logger);
                    }
                    else
                    {
                        NatsLog.NotConnected(_logger);
                    }
                    await SubscribeToAll();
                    await SubscribeToGroupManagementChannel();
                    await SubscribeToAckChannel();
                    await SubscribeToReturnResultsAsync();
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }
    }

    private static string GenerateServerName()
    {
        // Use the machine name for convenient diagnostics, but add a guid to make it unique.
        // Example: MyServerName_02db60e5fab243b890a847fa5c4dcb29
        return $"{Environment.MachineName}_{Guid.NewGuid():N}";
    }

    private static string GenerateInvocationId()
    {
        Span<byte> buffer = stackalloc byte[16];
        var success = Guid.NewGuid().TryWriteBytes(buffer);
        Debug.Assert(success);
        // 16 * 4/3 = 21.333 which means base64 encoding will use 22 characters of actual data and 2 characters of padding ('=')
        Span<char> base64 = stackalloc char[24];
        success = Convert.TryToBase64Chars(buffer, base64, out var written);
        Debug.Assert(success);
        Debug.Assert(written == 24);
        // Trim the two '=='
        Debug.Assert(base64.EndsWith("=="));
        return new string(base64[..^2]);
    }

    private sealed class LoggerTextWriter : TextWriter
    {
        private readonly ILogger _logger;

        public LoggerTextWriter(ILogger logger)
        {
            _logger = logger;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {

        }

        public override void WriteLine(string? value)
        {
            NatsLog.ConnectionMultiplexerMessage(_logger, value);
        }
    }

    private interface INatsFeature
    {
        HashSet<string> Groups { get; }
    }

    private sealed class NatsFeature : INatsFeature
    {
        public HashSet<string> Groups { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}