// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

internal abstract class AbstractLanguageServer<TRequestContext>
{
    private readonly JsonRpc _jsonRpc;

    /// <summary>
    /// Lazy as construction requires access to the lazy <see cref="_lspServices"/>  
    /// </summary>
    protected readonly Lazy<ILspLogger> Logger;

    /// <summary>
    /// These are lazy to allow implementations to define custom variables that are used by
    /// <see cref="ConstructRequestExecutionQueue"/> or <see cref="ConstructLspServices"/>
    /// </summary>
    private readonly Lazy<IRequestExecutionQueue<TRequestContext>> _queue;
    private readonly Lazy<ILspServices> _lspServices;
    private readonly Lazy<AbstractHandlerProvider> _handlerProvider;

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Ensures that we only run shutdown and exit code once in order.
    /// Guards access to <see cref="_shutdownRequestTask"/> and <see cref="_exitNotificationTask"/>
    /// </summary>
    private readonly object _lifeCycleLock = new();

    /// <summary>
    /// Task representing the work done on LSP server shutdown.
    /// </summary>
    private Task? _shutdownRequestTask;

    /// <summary>
    /// Task representing the work down on LSP exit.
    /// </summary>
    private Task? _exitNotificationTask;

    /// <summary>
    /// Task completion source that is started when the server starts and completes when the server exits.
    /// Used when callers need to wait for the server to cleanup.
    /// </summary>
    private readonly TaskCompletionSource<object?> _serverExitedSource = new();

    public AbstractTypeRefResolver TypeRefResolver { get; }

    /// <summary>
    /// Invoked once, synchronously awaited immediately before the <see cref="JsonRpc"/> connection is torn down
    /// in <see cref="ExitAsync"/> -- but only when exit was requested by the client itself (an <c>exit</c>
    /// notification actually being processed), not when <see cref="ExitAsync"/> is instead reached via
    /// <see cref="JsonRpc_Disconnected"/> reacting to a lost/broken connection. Both paths can otherwise look
    /// identical from outside this class (e.g. to a raw byte-stream copier sitting on top of the same
    /// transport): a graceful close is a graceful close either way once the underlying stream is disposed. A
    /// caller that owns the raw transport can use this hook to write a final, out-of-band signal distinguishing
    /// "the client asked us to exit and we're doing so cleanly" from "the connection just broke and we're
    /// cleaning up in reaction" before that distinction disappears into an ordinary stream closure. Optional;
    /// a server that doesn't need this distinction (most) can leave it unset.
    /// </summary>
    public Func<Task>? OnClientRequestedExitAsync { get; set; }

    protected AbstractLanguageServer(
        JsonRpc jsonRpc,
        AbstractTypeRefResolver? typeRefResolver)
    {
        _jsonRpc = jsonRpc;
        TypeRefResolver = typeRefResolver ?? TypeRef.DefaultResolver.Instance;

        // We have no need to continue running LSP requests after the connection is closed.
        _jsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;

        _jsonRpc.AddLocalRpcTarget(this);
        _jsonRpc.Disconnected += JsonRpc_Disconnected;
        _lspServices = new Lazy<ILspServices>(() => ConstructLspServices());
        Logger = new Lazy<ILspLogger>(() => GetLspServices().GetRequiredService<ILspLogger>());
        _queue = new Lazy<IRequestExecutionQueue<TRequestContext>>(() => ConstructRequestExecutionQueue());
        _handlerProvider = new Lazy<AbstractHandlerProvider>(() =>
        {
            var lspServices = _lspServices.Value;
            var handlerProvider = new HandlerProvider(lspServices, TypeRefResolver);
            SetupRequestDispatcher(handlerProvider);
            return handlerProvider;
        });
    }

    /// <summary>
    /// Initializes the LanguageServer.
    /// </summary>
    /// <remarks>Should be called at the bottom of the implementing constructor or immediately after construction.</remarks>
    public void Initialize()
    {
        GetRequestExecutionQueue();
    }

    /// <summary>
    /// Extension point to allow creation of <see cref="ILspServices"/> since that can't always be handled in the constructor.
    /// </summary>
    /// <returns>An <see cref="ILspServices"/> instance for this server.</returns>
    /// <remarks>This should only be called once, and then cached.</remarks>
    protected abstract ILspServices ConstructLspServices();

    protected virtual AbstractHandlerProvider HandlerProvider
    {
        get
        {
            return _handlerProvider.Value;
        }
    }

    public ILspServices GetLspServices() => _lspServices.Value;

    protected virtual void SetupRequestDispatcher(AbstractHandlerProvider handlerProvider)
    {
        // Get unique set of methods from the handler provider for the default language.
        foreach (var methodGroup in handlerProvider
            .GetRegisteredMethods()
            .GroupBy(m => m.MethodName))
        {
            // Instead of concretely defining methods for each LSP method, we instead dynamically construct the
            // generic method info from the exported handler types.  This allows us to define multiple handlers for
            // the same method but different type parameters.  This is a key functionality to support LSP extensibility
            // in cases like XAML, TS to allow them to use different LSP type definitions

            // Verify that we are not mixing different numbers of request parameters and responses between different language handlers
            // e.g. it is not allowed to have a method have both a parameterless and regular parameter handler.
            var requestTypes = methodGroup.Select(m => m.RequestTypeRef);
            var responseTypes = methodGroup.Select(m => m.ResponseTypeRef);
            if (!AllTypesMatch(requestTypes))
            {
                throw new InvalidOperationException($"Language specific handlers for {methodGroup.Key} have mis-matched number of parameters:{Environment.NewLine}{string.Join(Environment.NewLine, methodGroup)}");
            }

            if (!AllTypesMatch(responseTypes))
            {
                throw new InvalidOperationException($"Language specific handlers for {methodGroup.Key} have mis-matched number of returns:{Environment.NewLine}{string.Join(Environment.NewLine, methodGroup)}");
            }

            var delegatingEntryPoint = CreateDelegatingEntryPoint(methodGroup.Key);
            var methodAttribute = new JsonRpcMethodAttribute(methodGroup.Key)
            {
                UseSingleObjectParameterDeserialization = true,
            };

            // We verified above that parameters match, set flag if this request has parameters or is parameterless so we can set the entrypoint correctly.
            var hasParameters = methodGroup.First().RequestTypeRef != null;
            var entryPoint = delegatingEntryPoint.GetEntryPoint(hasParameters);
            _jsonRpc.AddLocalRpcMethod(entryPoint, delegatingEntryPoint, methodAttribute);
        }

        static bool AllTypesMatch(IEnumerable<TypeRef?> typeRefs)
        {
            if (typeRefs.All(r => r is null) || typeRefs.All(r => r is not null))
            {
                return true;
            }

            return false;
        }
    }

    [JsonRpcMethod("shutdown")]
    public Task HandleShutdownRequestAsync(CancellationToken _) => ShutdownAsync();

    [JsonRpcMethod("exit")]
    public Task HandleExitNotificationAsync(CancellationToken _) => ExitAsync(requestedByClient: true);

    public virtual void OnInitialized()
    {
        IsInitialized = true;
    }

    protected virtual IRequestExecutionQueue<TRequestContext> ConstructRequestExecutionQueue()
    {
        var handlerProvider = HandlerProvider;
        var queue = new RequestExecutionQueue<TRequestContext>(this, handlerProvider);

        queue.Start();

        return queue;
    }

    protected IRequestExecutionQueue<TRequestContext> GetRequestExecutionQueue()
    {
        return _queue.Value;
    }

    public virtual bool TryGetLanguageForRequest(string methodName, object? serializedRequest, [NotNullWhen(true)] out string? language)
    {
        Logger.Value.LogDebug($"Using default language handler for {methodName}");
        language = LanguageServerConstants.DefaultLanguageName;
        return true;
    }

    protected abstract DelegatingEntryPoint CreateDelegatingEntryPoint(string method);

    public abstract TRequest DeserializeRequest<TRequest>(object? serializedRequest, RequestHandlerMetadata metadata);

    protected abstract class DelegatingEntryPoint
    {
        protected readonly string _method;

        public DelegatingEntryPoint(string method)
        {
            _method = method;
        }

        public abstract MethodInfo GetEntryPoint(bool hasParameter);

        protected async Task<object?> InvokeAsync(
            IRequestExecutionQueue<TRequestContext> queue,
            object? requestObject,
            ILspServices lspServices,
            CancellationToken cancellationToken)
        {
            var result = await queue.ExecuteAsync(requestObject, _method, lspServices, cancellationToken).ConfigureAwait(false);
            if (result == NoValue.Instance)
            {
                return null;
            }
            else
            {
                return result;
            }
        }
    }

    /// <summary>
    /// Waits for the server to exit. Unlike <see cref="EnsureExitAsync"/>, this does not require
    /// that a prior shutdown request was received - it can safely be awaited from server startup
    /// and will simply remain incomplete until exit actually happens (either via the LSP
    /// <c>exit</c> notification, or via the framework's JSON-RPC disconnect handling).
    /// </summary>
    public Task WaitForExitAsync()
    {
        return _serverExitedSource.Task;
    }

    /// <summary>
    /// Like <see cref="WaitForExitAsync"/>, but throws <see cref="ServerNotShutDownException"/>
    /// if the server has not yet been asked to shut down. Useful for callers that need to assert
    /// a prior shutdown request as part of their lifecycle contract.
    /// </summary>
    public Task EnsureExitAsync()
    {
        lock (_lifeCycleLock)
        {
            // Ensure we've actually been asked to shutdown before waiting.
            if (_shutdownRequestTask == null)
            {
                throw new ServerNotShutDownException("The language server has not yet been asked to shutdown.");
            }
        }

        // Note - we return the _serverExitedSource task here instead of the _exitNotification task as we may not have
        // finished processing the exit notification before a client calls into us asking to restart.
        // This is because unlike shutdown, exit is a notification where clients do not need to wait for a response.
        return _serverExitedSource.Task;
    }

    /// <summary>
    /// Tells the LSP server to stop handling any more incoming messages (other than exit).
    /// Typically called from an LSP shutdown request.
    /// </summary>
    public Task ShutdownAsync(string message = "Shutting down")
    {
        Task shutdownTask;
        lock (_lifeCycleLock)
        {
            // Run shutdown or return the already running shutdown request.
            _shutdownRequestTask ??= Shutdown_NoLockAsync(message);
            shutdownTask = _shutdownRequestTask;
            return shutdownTask;
        }

        // Runs the actual shutdown outside of the lock - guaranteed to be only called once by the above code.
        async Task Shutdown_NoLockAsync(string message)
        {
            // Immediately yield so that this does not run under the lock.
            await Task.Yield();

            Logger.Value.LogInformation(message);

            // Allow implementations to do any additional cleanup on shutdown.
            var shutdownHooks = GetLspServices().GetRequiredServices<IOnServerShutdown>();
            foreach (var hook in shutdownHooks)
            {
                await hook.ShutdownAsync().ConfigureAwait(false);
            }

            await ShutdownRequestExecutionQueueAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells the LSP server to exit.  Requires that <see cref="ShutdownAsync(string)"/> was called first.
    /// Typically called from an LSP exit notification.
    /// </summary>
    /// <param name="shutdownException">Optional exception that caused the server to shutdown.
    /// When provided, <see cref="WaitForExitAsync"/> will throw this exception so callers can observe the error.</param>
    /// <param name="requestedByClient">Whether this call is reached because the client's own <c>exit</c>
    /// notification was received and processed (<see cref="HandleExitNotificationAsync"/>), as opposed to
    /// <see cref="JsonRpc_Disconnected"/> reacting to a lost/broken connection. See
    /// <see cref="OnClientRequestedExitAsync"/>.</param>
    public Task ExitAsync(Exception? shutdownException = null, bool requestedByClient = false)
    {
        Task exitTask;
        lock (_lifeCycleLock)
        {
            if (_shutdownRequestTask?.IsCompleted != true)
            {
                throw new ServerNotShutDownException("The language server has not yet been asked to shutdown or has not finished shutting down.");
            }

            // Run exit or return the already running exit request. Note: if JsonRpc_Disconnected's call races
            // this one and wins, requestedByClient below is lost -- that's fine, since it means the connection
            // broke before the client's own exit notification actually finished being processed, so treating it
            // as not-client-requested is correct.
            _exitNotificationTask ??= Exit_NoLockAsync();
            exitTask = _exitNotificationTask;
            return exitTask;
        }

        // Runs the actual exit outside of the lock - guaranteed to be only called once by the above code.
        async Task Exit_NoLockAsync()
        {
            // Immediately yield so that this does not run under the lock.
            await Task.Yield();

            try
            {
                var lspServices = GetLspServices();

                // Allow implementations to do any additional cleanup on exit.
                var exitHooks = lspServices.GetRequiredServices<IOnServerShutdown>();
                foreach (var hook in exitHooks)
                {
                    await hook.ExitAsync().ConfigureAwait(false);
                }

                var queueFullyDrained = await ShutdownRequestExecutionQueueAsync().ConfigureAwait(false);

                await lspServices.DisposeAsync().ConfigureAwait(false);

                // Only invoke this if the request execution queue actually finished draining -- e.g.
                // LanguageServerHost's clean-exit sentinel, written directly to the raw transport bypassing
                // StreamJsonRpc's own serialized writer (see CleanExitSentinel's remarks), would otherwise be
                // racing whatever in-flight response write the queue gave up waiting for, corrupting LSP
                // framing if the sentinel interleaves with it. If the drain timed out with work still
                // outstanding, skip the courtesy signal entirely rather than risk that race -- that work still
                // completes on its own schedule in the background regardless.
                if (requestedByClient && queueFullyDrained && OnClientRequestedExitAsync is { } onClientRequestedExitAsync)
                {
                    try
                    {
                        // Best-effort: this is a courtesy signal on top of a shutdown that's happening either
                        // way, so a failure here (e.g. the transport already broke for an unrelated reason)
                        // must not prevent the JsonRpc teardown below.
                        await onClientRequestedExitAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                _jsonRpc.Disconnected -= JsonRpc_Disconnected;
                _jsonRpc.Dispose();
            }
            catch (Exception)
            {
                // Swallow exceptions thrown by disposing our JsonRpc object. Disconnected events can potentially throw their own exceptions so
                // we purposefully ignore all of those exceptions in an effort to shutdown gracefully.
            }
            finally
            {
                if (shutdownException is not null)
                {
                    _serverExitedSource.TrySetException(shutdownException);
                }
                else
                {
                    _serverExitedSource.TrySetResult(null);
                }
            }
        }
    }

    private ValueTask<bool> ShutdownRequestExecutionQueueAsync()
    {
        var queue = GetRequestExecutionQueue();
        return queue.DrainAndDisposeAsync();
    }

    /// <summary>
    /// Cleanup the server if we encounter a json rpc disconnect so that we can be restarted later.
    /// </summary>
    private void JsonRpc_Disconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        JsonRpc_DisconnectedAsync(sender, e).Forget();

        async Task JsonRpc_DisconnectedAsync(object? sender, JsonRpcDisconnectedEventArgs e)
        {
            var exceptionToReport = TryGetReportableException(e);

            // It is possible this gets called during normal shutdown and exit.
            // ShutdownAsync and ExitAsync will no-op if shutdown was already triggered by something else.
            await ShutdownAsync(message: $"Shutdown triggered by JsonRpc disconnect {e.Reason}").ConfigureAwait(false);
            await ExitAsync(exceptionToReport).ConfigureAwait(false);
        }

        Exception? TryGetReportableException(JsonRpcDisconnectedEventArgs e)
        {
            if (e.Exception == null)
            {
                return null;
            }

            if (e.Reason == DisconnectedReason.RemotePartyTerminated || e.Reason == DisconnectedReason.LocallyDisposed)
            {
                // These are expected disconnect reasons that can occur during normal shutdown or if the client disconnects.
                return null;
            }

            if (e.Exception is IOException)
            {
                // Server communication is done over named pipes, IO exceptions are normal if the client disconnects unexpectedly while the server is in the middle of reading or writing.
                return null;
            }

            return e.Exception;
        }
    }

    internal TestAccessor GetTestAccessor()
    {
        return new(this);
    }

    internal readonly struct TestAccessor
    {
        private readonly AbstractLanguageServer<TRequestContext> _server;

        internal TestAccessor(AbstractLanguageServer<TRequestContext> server)
        {
            _server = server;
        }

        public T GetRequiredLspService<T>() where T : class => _server.GetLspServices().GetRequiredService<T>();

        internal RequestExecutionQueue<TRequestContext>.TestAccessor? GetQueueAccessor()
        {
            if (_server._queue.Value is RequestExecutionQueue<TRequestContext> requestExecution)
                return requestExecution.GetTestAccessor();

            return null;
        }

        internal JsonRpc GetServerRpc() => _server._jsonRpc;

        internal bool HasShutdownStarted()
        {
            return GetShutdownTaskAsync() != null;
        }

        internal Task? GetShutdownTaskAsync()
        {
            lock (_server._lifeCycleLock)
            {
                return _server._shutdownRequestTask;
            }
        }
    }
}
