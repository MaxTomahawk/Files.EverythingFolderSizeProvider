using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace Files.EverythingFolderSizeProvider;

internal static partial class EverythingBridge
{
    private const int ProtocolVersion = 1;
    private const string ServiceName = "com.files.folderSizeProvider";
    private static readonly object ClientLock = new();
    private static nint everythingClient;

    public static async Task RunAsync()
    {
        using var connection = new AppServiceConnection
        {
            AppServiceName = ServiceName,
            PackageFamilyName = Package.Current.Id.FamilyName,
        };

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RequestReceived += OnRequestReceived;
        connection.ServiceClosed += (_, _) => closed.TrySetResult();

        var status = await connection.OpenAsync();
        if (status is not AppServiceConnectionStatus.Success)
            return;

        var registration = await connection.SendMessageAsync(new ValueSet
        {
            ["protocolVersion"] = ProtocolVersion,
            ["role"] = "bridge",
        });

        if (registration.Status is not AppServiceResponseStatus.Success)
            return;

        try
        {
            await closed.Task;
        }
        finally
        {
            lock (ClientLock)
                ResetEverythingClient();
        }
    }

    private static async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await args.Request.SendResponseAsync(HandleRequest(args.Request.Message));
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

    private static ValueSet HandleRequest(ValueSet request)
    {
        if (!TryGetInt32(request, "protocolVersion", out var protocolVersion) || protocolVersion != ProtocolVersion ||
            !TryGetString(request, "command", out var command) || !string.Equals(command, "getFolderSize", StringComparison.Ordinal) ||
            !TryGetString(request, "path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return CreateResponse("invalidRequest");
        }

        try
        {
            path = NormalizeFolderPath(path);
            lock (ClientLock)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (!EnsureEverythingClient())
                        return CreateResponse("unavailable");

                    if (!EverythingNative.IsDbLoaded(everythingClient))
                    {
                        if (EverythingNative.GetLastError() != 0)
                        {
                            ResetEverythingClient();
                            continue;
                        }

                        return CreateResponse("unavailable");
                    }

                    var size = EverythingNative.GetFolderSizeFromFilename(everythingClient, path);
                    if (size != ulong.MaxValue)
                        return CreateResponse("ok", size);

                    if (EverythingNative.GetLastError() != 0)
                    {
                        ResetEverythingClient();
                        continue;
                    }

                    return CreateResponse("notIndexed");
                }

                return CreateResponse("unavailable");
            }
        }
        catch
        {
            return CreateResponse("error");
        }
    }

    private static bool EnsureEverythingClient()
    {
        if (everythingClient != 0)
            return true;

        everythingClient = EverythingNative.Connect(null);
        if (everythingClient == 0)
            everythingClient = EverythingNative.Connect("1.5a");

        return everythingClient != 0;
    }

    private static void ResetEverythingClient()
    {
        if (everythingClient == 0)
            return;

        _ = EverythingNative.DestroyClient(everythingClient);
        everythingClient = 0;
    }
    private static string NormalizeFolderPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static ValueSet CreateResponse(string status, ulong? size = null)
    {
        var response = new ValueSet
        {
            ["protocolVersion"] = ProtocolVersion,
            ["status"] = status,
        };

        if (size is ulong value)
            response["size"] = value.ToString(CultureInfo.InvariantCulture);

        return response;
    }

    private static bool TryGetString(ValueSet values, string key, out string value)
    {
        if (values.TryGetValue(key, out var raw) && raw is string text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt32(ValueSet values, string key, out int value)
    {
        if (values.TryGetValue(key, out var raw))
        {
            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }

            if (raw is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)longValue;
                return true;
            }
        }

        value = 0;
        return false;
    }

    [LibraryImport("Everything3.dll", EntryPoint = "Everything3_ConnectW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint Everything3Connect(string? instanceName);

    [LibraryImport("Everything3.dll", EntryPoint = "Everything3_GetFolderSizeFromFilenameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial ulong Everything3GetFolderSizeFromFilename(nint client, string path);

    [LibraryImport("Everything3.dll", EntryPoint = "Everything3_DestroyClient")]
    private static partial int Everything3DestroyClient(nint client);

    [LibraryImport("Everything3.dll", EntryPoint = "Everything3_IsDBLoaded")]
    private static partial int Everything3IsDbLoaded(nint client);

    [LibraryImport("Everything3.dll", EntryPoint = "Everything3_GetLastError")]
    private static partial uint Everything3GetLastError();

    private static class EverythingNative
    {
        public static nint Connect(string? instanceName) => Everything3Connect(instanceName);
        public static int DestroyClient(nint client) => Everything3DestroyClient(client);
        public static bool IsDbLoaded(nint client) => Everything3IsDbLoaded(client) != 0;
        public static uint GetLastError() => Everything3GetLastError();
        public static ulong GetFolderSizeFromFilename(nint client, string path) => Everything3GetFolderSizeFromFilename(client, path);
    }
}
