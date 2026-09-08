using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;

namespace Files.EverythingFolderSizeProvider.Service;

public sealed class FolderSizeServiceTask : IBackgroundTask
{
    private const int ProtocolVersion = 1;
    private const string ServiceName = "com.files.folderSizeProvider";
    private const string BridgeRole = "bridge";
    private const string BridgeParameterGroup = "bridge";

    private static readonly object BridgeStateLock = new();
    private static readonly SemaphoreSlim BridgeLaunchGate = new(1, 1);
    private static AppServiceConnection? bridgeConnection;
    private static TaskCompletionSource<AppServiceConnection> bridgeReady = CreateBridgeReadySource();

    private BackgroundTaskDeferral? serviceDeferral;
    private AppServiceConnection? serviceConnection;

    public void Run(IBackgroundTaskInstance taskInstance)
    {
        serviceDeferral = taskInstance.GetDeferral();
        taskInstance.Canceled += OnTaskCanceled;

        if (taskInstance.TriggerDetails is not AppServiceTriggerDetails details)
        {
            CompleteServiceDeferral();
            return;
        }

        serviceConnection = details.AppServiceConnection;
        serviceConnection.RequestReceived += OnRequestReceived;
        serviceConnection.ServiceClosed += OnServiceClosed;
    }

    private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = args.Request.Message;
            if (IsBridgeRegistration(request))
            {
                RegisterBridge(sender);
                await args.Request.SendResponseAsync(CreateResponse("bridgeReady"));
                return;
            }

            var response = await ForwardToBridgeAsync(request);
            await args.Request.SendResponseAsync(response);
        }
        catch
        {
            try
            {
                await args.Request.SendResponseAsync(CreateResponse("error"));
            }
            catch
            {
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsBridgeRegistration(ValueSet request)
        => request.TryGetValue("role", out var role) &&
           string.Equals(role as string, BridgeRole, StringComparison.Ordinal) &&
           TryGetProtocolVersion(request, out var version) && version == ProtocolVersion;

    private static async Task<ValueSet> ForwardToBridgeAsync(ValueSet request)
    {
        if (!TryGetProtocolVersion(request, out var protocolVersion) || protocolVersion != ProtocolVersion)
            return CreateResponse("invalidRequest");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bridge = await EnsureBridgeAsync();
            if (bridge is null)
                return CreateResponse("unavailable");

            try
            {
                var response = await bridge.SendMessageAsync(request);
                if (response.Status is AppServiceResponseStatus.Success)
                    return response.Message;
            }
            catch
            {
            }

            InvalidateBridge(bridge);
        }

        return CreateResponse("unavailable");
    }

    private static async Task<AppServiceConnection?> EnsureBridgeAsync()
    {
        lock (BridgeStateLock)
        {
            if (bridgeConnection is not null)
                return bridgeConnection;
        }

        await BridgeLaunchGate.WaitAsync();
        try
        {
            lock (BridgeStateLock)
            {
                if (bridgeConnection is not null)
                    return bridgeConnection;
            }

            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync(BridgeParameterGroup);
            }
            catch
            {
                return null;
            }

            Task<AppServiceConnection> waitTask;
            lock (BridgeStateLock)
                waitTask = bridgeReady.Task;

            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(10)));
            return completed == waitTask ? await waitTask : null;
        }
        finally
        {
            BridgeLaunchGate.Release();
        }
    }

    private static void RegisterBridge(AppServiceConnection connection)
    {
        lock (BridgeStateLock)
        {
            if (bridgeConnection is not null && !ReferenceEquals(bridgeConnection, connection))
                bridgeConnection.Dispose();

            bridgeConnection = connection;
            bridgeReady.TrySetResult(connection);
        }
    }

    private static void InvalidateBridge(AppServiceConnection connection)
    {
        lock (BridgeStateLock)
        {
            if (!ReferenceEquals(bridgeConnection, connection))
                return;

            bridgeConnection = null;
            bridgeReady = CreateBridgeReadySource();
            connection.Dispose();
        }
    }

    private static TaskCompletionSource<AppServiceConnection> CreateBridgeReadySource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool TryGetProtocolVersion(ValueSet request, out int version)
    {
        if (request.TryGetValue("protocolVersion", out var raw))
        {
            if (raw is int intValue)
            {
                version = intValue;
                return true;
            }

            if (raw is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
            {
                version = (int)longValue;
                return true;
            }
        }

        version = 0;
        return false;
    }

    private static ValueSet CreateResponse(string status)
        => new()
        {
            ["protocolVersion"] = ProtocolVersion,
            ["status"] = status,
            ["service"] = ServiceName,
        };

    private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
    {
        lock (BridgeStateLock)
        {
            if (ReferenceEquals(bridgeConnection, sender))
            {
                bridgeConnection = null;
                bridgeReady = CreateBridgeReadySource();
            }
        }

        CompleteServiceDeferral();
    }

    private void OnTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        => CompleteServiceDeferral();

    private void CompleteServiceDeferral()
    {
        if (serviceConnection is not null)
        {
            serviceConnection.RequestReceived -= OnRequestReceived;
            serviceConnection.ServiceClosed -= OnServiceClosed;
            serviceConnection = null;
        }

        serviceDeferral?.Complete();
        serviceDeferral = null;
    }
}
