# Everything Folder Sizes for Files

A folder-size provider add-on for [Files](https://github.com/files-community/Files) backed by the Everything 1.5 SDK3 index.

## Why

Files can calculate folder sizes by walking the filesystem. This provider instead asks Everything for its already indexed folder-size value, avoiding recursive scans when an indexed value is available.

## Architecture

- Files discovers providers through the Windows App Extension contract `com.files.folderSizeProvider`.
- This package declares that app extension and exposes an App Service facade with the same name.
- The App Service launches a hidden full-trust bridge in this package. The bridge is required because Everything SDK3 IPC is not available from the App Service AppContainer.
- The bridge keeps one Everything SDK3 client connected and returns indexed folder sizes through the App Service contract.
- A disconnected bridge is relaunched automatically. A stale SDK3 client is discarded and reconnected when Everything is restarted.
- Files falls back to its built-in calculator when the provider is unavailable or a path has no indexed folder size.

## Requirements

- Windows 10 2004 (19041) or newer.
- Everything 1.5. SDK3 only works with Everything 1.5; this provider has been tested with Everything 1.5.0.1423b.
- `Index file size` and `Index folder size` enabled in Everything.
- A Files build containing the `com.files.folderSizeProvider` extension host.

## Protocol v1

Request `ValueSet`:

- `protocolVersion`: `1`
- `command`: `getFolderSize`
- `path`: absolute folder path

Response `ValueSet`:

- `protocolVersion`: `1`
- `status`: `ok`, `notIndexed`, `unavailable`, `invalidRequest`, or `error`
- `size`: decimal byte count as a string when `status` is `ok`

Status semantics:

- `ok`: an indexed folder size is available.
- `notIndexed`: Everything is connected and loaded, but no indexed folder size is available for the path.
- `unavailable`: Everything, its database, or the bridge is temporarily unavailable. The bridge retries stale connections automatically.
- `invalidRequest`: the request does not match protocol v1.
- `error`: the provider could not process the request.

## Everything SDK3

The native SDK3 binaries are redistributed under the permissive voidtools license in `LICENSE-voidtools-SDK3.txt`. Architecture-specific DLLs live under `native/`.

## Build

Build the solution for one concrete platform, for example x64:

```powershell
msbuild Files.EverythingFolderSizeProvider.slnx -restore -p:Configuration=Debug -p:Platform=x64
```

The solution has also been verified to compile for `x86` and `arm64`.

For local development, register the generated x64 package layout with PowerShell after building:

```powershell
Add-AppxPackage -Register .\src\Files.EverythingFolderSizeProvider\bin\x64\Debug\net10.0-windows10.0.26100.0\AppxManifest.xml
```

## Validation

On the development machine used for the initial implementation:

- direct persistent SDK3 folder-size calls were approximately 48-100 microseconds each;
- full App Extension -> App Service -> full-trust bridge -> SDK3 round trips were typically around 0.3 milliseconds each;
- a 250-request parallel burst completed with zero failed requests;
- missing paths return `notIndexed`;
- stopping Everything returns `unavailable` without terminating the bridge;
- restarting Everything reconnects from the same bridge process;
- forcibly terminating the bridge causes it to be relaunched on the next request.

These numbers are measurements from one machine, not performance guarantees.

## License

Project code is MIT licensed. Everything SDK3 remains copyright voidtools / David Carpenter and is covered by its bundled license.
