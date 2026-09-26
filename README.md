# Mainframe

A modular mainframe-style computer environment with a local authenticated kernel.
Requires the .NET 10 SDK (10.0.400 feature band). Building the full solution also
requires installed WinFsp 2.1.25156 with its .NET binding; see
[Windows drive and directory adapter](#windows-drive-and-directory-adapter).

See [plan.md](plan.md) for the architecture and milestones, and [packets.md](packets.md)
for the wire protocol. The current execution milestone is tested on Windows only.

## Run

```powershell
./native/build.ps1 # First build only; requires Visual Studio 2022 C++ tools
dotnet build Mainframe.slnx
dotnet run --project src/Mainframe.Cli -- cluster init --name atlas
dotnet run --project src/Mainframe.Host -- serve
```

Keep the host running and use a second terminal:

```powershell
dotnet run --project src/Mainframe.Cli -- status
dotnet run --project src/Mainframe.Cli -- health --json
dotnet run --project src/Mainframe.Cli -- capabilities --json
```

Initialization runs once and refuses to overwrite existing directories. It creates
stable kernel/mainframe IDs, SQLite metadata, a private CA, and the initial local
operator certificate under `%LOCALAPPDATA%\Mainframe`. The directory is restricted
to your Windows account and SYSTEM. Nothing is added to the Windows certificate
trust store. This development host runs as your account; a dedicated service
account/installable Windows service is not configured yet.

The host listens only on `127.0.0.1:7443`, using TLS 1.3 and the enrolled local
operator certificate. No firewall rules or public listeners are created. Stop it
with Ctrl+C. Use matching explicit paths/ports for another local test instance:

```powershell
dotnet run --project src/Mainframe.Cli -- cluster init --state "$env:LOCALAPPDATA\Mainframe-Test" --name test
dotnet run --project src/Mainframe.Host -- serve --state "$env:LOCALAPPDATA\Mainframe-Test" --port 7444
dotnet run --project src/Mainframe.Cli -- --state "$env:LOCALAPPDATA\Mainframe-Test" --endpoint 127.0.0.1:7444 status
```

Leaf certificates expire after seven days. Stop `mframed`, then run `mframe cluster renew`
during their final 24 hours. Renewal preserves the CA, kernel, and operator identity.
It refuses revoked or expired credentials. Network enrollment and expired-identity
recovery are not implemented; preserve existing state rather than deleting it.

To package the CLI as a repository-local `mframe` tool:

```powershell
dotnet pack src/Mainframe.Cli -c Release -o artifacts/packages
dotnet tool install Mainframe.Cli --version 0.1.0 --add-source artifacts/packages --tool-path artifacts/tools
./artifacts/tools/mframe help
```

All projects build into a shared directory, including their runtime dependencies:
`bin/Debug/net10.0` by default, or `bin/Release/net10.0` with `-c Release`.
This contains `mframe.exe`, `mframed.exe`, `Mainframe.Hello.exe`, and `Mainframe.Fixtures.exe`. Intermediate `obj`
files remain separate for each project.

Add the Debug directory to PATH for the current PowerShell session:

```powershell
dotnet build Mainframe.slnx
$env:PATH = "$(Resolve-Path ./bin/Debug/net10.0);$env:PATH"
mframe help
mframed help
```

To keep it across terminals, add `E:\Projects\Mainframe\bin\Debug\net10.0` to your
user PATH. The `Mainframe.Hello` example needs kernel bootstrap, so run it through
`mframe` after registration, rather than launching it directly.

## Commands

| Command | Purpose |
| --- | --- |
| `mframe help [command]` | List commands or describe one command |
| `mframe cluster init` | Create private local kernel state and initial credentials |
| `mframe status` | Query the running kernel's identity and health |
| `mframe health` | Query readiness, uptime, and connections |
| `mframe capabilities` | List the kernel's implemented syscall contracts |
| `mframe version` | Print the CLI version |
| `mframe cluster renew` | Renew eligible unexpired local certificates with the host stopped |
| `mframe program register <manifest.json>` | Register or revise an installed program |
| `mframe program list` / `remove <qualified-name>` | Inspect or remove registrations |
| `mframe host-root add <name> <absolute-directory>` | Approve a named shell working directory |
| `mframe host-root list` / `remove <name>` | Inspect or remove root mappings |
| `mframe exec [options] <program> [arguments...]` | Relay stdin/stdout/stderr and return the child exit code |
| `mframe exec --terminal <program> [arguments...]` | Run through Windows ConPTY |
| `mframe connect` | Open the kernel-managed interactive shell |

No arguments shows help. `--help`, `-h`, `--version`, and per-command `--help` are supported.
Query commands support `--state`, `--endpoint`, and `--json` before or after the
command. Endpoints are restricted to loopback for this milestone.
Exit codes: `0` success, `1` operation failure, `2` invalid usage, `130` cancelled.
Errors go to stderr; normal output goes to stdout. A successfully launched `mframe exec`
returns the child's exit code, including nonzero codes. Put Mainframe options before
the program selector: every argument after it belongs to the child, including
`--help`, `--name`, and empty strings. Terminal mode requires an actual console.

## Register and run a program

Build the solution, then save this example as `hello.json`. Replace both paths with
absolute paths on your machine. It uses the standalone SDK example in `examples/Mainframe.Hello`:

```json
{
  "manifestVersion": 1,
  "identity": "examples/hello",
  "version": "1.0.0",
  "executable": "E:\\Projects\\Mainframe\\bin\\Debug\\net10.0\\Mainframe.Hello.exe",
  "fixedArguments": [],
  "workingDirectory": "E:\\Projects\\Mainframe",
  "operatingSystems": ["windows"],
  "architectures": ["x64"],
  "runtime": "dotnet10",
  "ioModes": ["pipes", "terminal"],
  "environmentAllowlist": [],
  "permissions": ["kernel.describe"],
  "hostRoots": ["workspace"]
}
```

With the host running, use the built CLI (or its installed `mframe` tool):

```powershell
$mframe = './bin/Debug/net10.0/mframe.exe'
& $mframe program register ./hello.json
& $mframe host-root add workspace 'E:\Projects\Mainframe'
& $mframe exec hello Alice
# Hello returns exit code 0 on success.
& $mframe connect
```

Inside the mainframe shell:

```text
cd /host/workspace
pwd
programs
hello "Alice Smith"
exit
```

The kernel parses commands, stores shell state, and resolves programs. Short names
must identify exactly one eligible registration. Ambiguity returns candidates;
use `namespace/name@version#kernel-id` to select one. Each execution retains its
registration revision. No executable deployment occurs.

At `/` or `/host`, programs use their manifest working directory. Under a named
root, both the manifest and operator grants must permit that root. Roots support
working-directory navigation only; they do not provide file RPCs, shared volumes,
or Windows filesystem mounts. Junctions and other reparse points are rejected.
Programs are trusted and run with your Windows account's host access.

Register native executables directly. For a .NET DLL, set `interpreter` to the
absolute `dotnet.exe` path and `executable` to the DLL. For scripts, register an
explicit interpreter. Fixed manifest arguments precede user arguments. The child
environment contains only allowlisted variables, Windows runtime variables
`SystemRoot`, `WINDIR`, `TEMP`, `TMP`, and the bootstrap endpoint/handle identifiers.
Runtime declarations currently recognize `native` and `dotnet10`; unavailable or
unsupported registrations stay visible and fail execution.

## Program SDK

Reference `src/Mainframe.Sdk/Mainframe.Sdk.csproj` from a normal .NET application:

```csharp
await using var kernel = await Mainframe.Sdk.MainframeProgram.ConnectAsync();
var identity = await kernel.DescribeAsync();
Console.WriteLine(identity.GetProperty("name").GetString());
```

Declare `kernel.describe` in the manifest permissions. The SDK reads a single-use
credential from a separate inherited pipe and opens its own TLS connection.
It never uses terminal bytes for RPC. The pipe also carries the trusted CA.
Credentials expire after 30 seconds; execution authorization lasts 60 seconds and
is renewed while the execution and its grants remain valid. Programs without
kernel calls do not need the SDK.

## Structure and limits

| Component | Responsibility |
| --- | --- |
| Mainframe.Protocol | Framing, explicit JSON contracts, multiplexing, credit and completion |
| Mainframe.Core | Private identity, SQLite migrations, registrations, roots, grants and execution records |
| Mainframe.Host | Loopback TLS, authentication, shell, Windows Job Objects and ConPTY |
| Mainframe.Client | Typed administration/execution APIs and independent I/O streams |
| Mainframe.Sdk | Inherited bootstrap and program-scoped kernel queries |
| Mainframe.Cli | Administration, queries, `exec`, interactive shell and console restoration |

Local defaults are 32 connections, eight concurrent TLS handshakes, 64 foreground
processes, 16 shell sessions per connection, 128 registered programs and host roots,
and a 4 KiB serialized manifest. RPC connections permit 128 active exchanges,
eight channels per exchange, and 4,096 active/unretired exchange records (lifetime for legacy peers). Streaming uses
64 KiB receive windows within a bounded connection budget. Queries and launches
have a ten-second client deadline; program runtime does not inherit that deadline.

Disconnect detection initiates foreground cleanup, with up to two seconds for
graceful termination before the Job Object is terminated. Host shutdown permits
a ten-second transport drain. Startup marks unfinished execution records
`outcome_unknown`; it never retries them. There are no durable background jobs yet.

State schemas 1 and 2 migrate transactionally to schema 3; volume mount records contain no passwords. Newer schemas are rejected.
The initial local operator has an explicit persisted wildcard grant. Program RPCs
are further restricted to declared kernel-query and volume-scoped permissions. Multi-user grant
administration, network enrollment/automatic renewal, peer delegation, Linux PTYs,
host-directory/remote filesystems, and public endpoint hardening remain later work.

## Encrypted local storage (Windows)

Encrypted SQLCipher containers (`.mfv`) mount at `/vol/<name>`. They hold filenames,
metadata, and 64 KiB content chunks. The SDK accesses this namespace over TLS;
ordinary `System.IO` still accesses the host disk. `/host` remains working-directory
navigation. There is no Windows drive export or remote-volume routing yet.

Build the native engine as described below, build the solution, and start the host.
The parent directory of the container must already exist on a fixed local disk:

```powershell
mframe volume create E:\Mainframe\Data\documents.mfv
mframe volume mount E:\Mainframe\Data\documents.mfv /vol/documents
mframe volume list
mframe volume unmount /vol/documents
```

Create prompts for a password and confirmation, then leaves the volume unmounted.
Mount prompts once. Passwords have no minimum length, password-specific maximum,
or character restrictions; empty passwords are accepted and still enable encryption.
Passwords are never trimmed or normalized. The normal RPC frame-size limit applies. There
are no password arguments, saved keys, automatic unlock, or password recovery.
Configured mounts restart locked. Unmount requires no open handles or in-flight
storage operations and never deletes the container.

WAL/SHM recovery sidecars are allowed while live or after a crash. Preserve them.
Copy the `.mfv` alone only after a successful clean unmount. Filenames/content are
encrypted; the host container path, mount name, UUID, file size, and recovery-file
headers are not secret. An unlocked kernel can see plaintext. Managed password
strings, paging, and crash dumps prevent a guarantee of complete memory erasure.

Programs declare `volume:<uuid>:read` and/or `volume:<uuid>:write` in manifest
permissions. Use the UUID printed by create/list. File access requires the
intersection with current operator grants. Programs cannot administer volumes.
`MainframeProgram.Files` provides async metadata and file operations:

```csharp
await using MainframeProgram kernel = await MainframeProgram.ConnectAsync();
await using KernelFileStream file = await kernel.Files.OpenAsync(
    "/vol/documents/notes.txt", "read-write", "open-or-create");
await file.WriteAsync("Hello from Mainframe"u8.ToArray());
await file.FlushAsync();
```

This snippet belongs inside an explicit `Program.Main`; import `Mainframe.Sdk`
and `Mainframe.Client`. Streams support seeking, length, truncation, synchronous
and asynchronous calls, and disposal. Prefer async calls. File content/metadata
are not cached across RPCs, and failed mutations are not automatically retried.

The solution builds standalone `Mainframe.Ls.exe`, `Mainframe.Cat.exe`, and
`Mainframe.StorageDemo.exe`. Register each as an installed program using the Hello
manifest pattern: change identity/executable, use empty fixed arguments, and declare
volume grants. Register no host roots unless needed for host working directories.
For identities `examples/ls`, `examples/cat`, and `examples/storage-demo`:

```powershell
mframe exec ls /vol/documents
mframe exec storage-demo /vol/documents
# The demo prints the path of its verified, atomically published binary file.
mframe exec cat /vol/documents/<printed-demo-directory>/nested/data.bin
```

`cat` sends raw bytes to stdout. Test it with redirection for binary data. The demo
creates a unique directory on each run; it does not overwrite existing documents.

Managed writes acknowledge after FULL transaction commit. Each RPC write is at
most 64 KiB; larger SDK writes split into separate commits. Publish complete files
through create-new temporary names, flush, close, and same-volume rename/replace.
Version-2 handles support byte-range locks. Cross-volume rename, links, and recursive deletion remain unsupported.
Deletion and replacement require delete sharing from every open handle. Successful
deletion or replacement invalidates version-1 handles to the removed entry.
Version-2 handles retain the detached object until final close.
See [plan.md](plan.md#local-encrypted-volumes) for lifecycle details.

## Native database build

The Windows x64 database engine is SQLCipher Community Edition 4.19.0 built with
OpenSSL 3.5.8. Run `./native/build.ps1` before the first managed build. It downloads
hash-pinned source archives and a project-local Windows Perl tool, requires Visual
Studio 2022 C++ build tools, and writes `artifacts/native/runtime/sqlite3.dll`.
The first build takes several minutes. Later .NET builds copy the runtime to the
shared output directory. There is no fallback to the ordinary SQLite bundle.
See `native/dependencies.json` for the pinned sources and compiler settings.

SQLCipher uses its BSD-style community license; OpenSSL uses Apache-2.0. Their
notices are in `native/SQLCipher-LICENSE.txt` and `native/OpenSSL-LICENSE.txt`.

## Verification

```powershell
dotnet test Mainframe.slnx -c Release
```

The full solution includes real WinFsp mount tests and requires the installed
runtime and .NET binding. To test only the kernel, execution, and storage backend
without WinFsp, use:

```powershell
dotnet test tests/Mainframe.Tests/Mainframe.Tests.csproj -c Release
```

Tests use isolated temporary state and real Windows processes, TLS connections,
Job Objects and ConPTY. They cover byte-exact streams, argument preservation,
scoped SDK calls, certificate renewal/recovery, migrations, credit violations,
slow consumers, cancellation, disconnect cleanup, terminal input/resize/Ctrl+C/EOF,
and CLI console restoration. Fixtures contain synthetic data only.
See `docs/protocol` for JSON schemas and golden wire fixtures. Linux has not been
validated and is not claimed as supported by this milestone.

## License

Mainframe is licensed under the [MIT License](LICENSE).
The CLI package includes the license and declares MIT in its NuGet metadata.

WinFsp is used by the separate Windows adapter; it is not a
dependency of the main CLI. Its [GPLv3 license with a FLOSS exception](https://github.com/winfsp/winfsp/blob/v2.1/License.txt)
allows qualifying open-source applications to link to its specified DLLs without
adopting GPLv3. The exception requires attribution in the user interface and
user-facing documentation, and prohibits linking or distributing the software
with proprietary software. It also permits redistribution of unmodified official
WinFsp installers. WinFsp itself retains its own license. Review the selected
release's terms when implementing and distributing the adapter.


## Virtual root and writable backend

After restarting your host with this build, `mframe exec ls` lists `/` by default:

```powershell
mframe exec ls
mframe exec ls /
mframe exec ls /vol
mframe exec ls /vol/documents
```

`/` contains `vol/`. `/vol` lists readable configured mounts, including locked ones.
Unlock a locked container with `mframe volume mount` as before. Container schema 1
is upgraded transactionally to schema 2 on mount; passwords and encryption settings
are unchanged. Keep the existing clean-unmount backup procedure. Do not copy only
the main container while it is mounted or discard recovery sidecars after a crash.

Existing SDK streams remain supported. Advanced clients can use directory and file
handles, explicit rights, metadata, allocation, atomic append, locks, and separate
cleanup/final-close operations:

```csharp
await using KernelFileHandle directory = await kernel.Files.OpenHandleAsync(
    new FsOpenV2("/vol/documents", Kind: "directory",
        Rights: ["list", "read-metadata"], Share: ["read", "write", "delete"]));
FsListing batch = await directory.EnumerateAsync();
```

`Mainframe.Client` supplies the handle and `Mainframe.Protocol` supplies its options.
Programs still need their volume UUID grants in the manifest. `DiscoverAsync`
reports capabilities and shared host capacity. Allocation growth does not reserve
physical space. Retained handles can read an old object after atomic replacement;
legacy stream handles keep their previous invalidation behavior. Cleanup releases
sharing and locks, while final disposal closes the object.

`KernelClient.ConnectFilesystemAsync` uses an operator certificate with a restricted
filesystem role. It cannot execute programs or administer volumes. Negotiated
exchange retirement allows connections to outlive 4,096 calls without reconnecting
or replaying mutations. The Windows adapter is described below; filesystem
navigation in the mainframe shell remains separate work.
The backend milestone passed 186 Release tests, including isolated host crash recovery and
a 257 MiB read after 10,050 queries on one connection. See
[the backend acceptance record](plan.md#virtual-root-and-writable-backend-september-2026)
for backend build/test evidence and the subsequent adapter acceptance record.

## Windows drive and directory adapter

`mframe-fs` is a separate foreground Windows x64 application. Install the official
WinFsp **2.1.25156** release with its .NET binding. The adapter finds WinFsp through
the installation registry and refuses other, unqualified versions. It does not
install drivers, store volume passwords, or load SQLCipher.

```powershell
dotnet build src/Mainframe.WinFsp
./bin/Debug/net10.0/mframe-fs.exe mount M:
# Or expose only one volume:
./bin/Debug/net10.0/mframe-fs.exe mount M: --root /vol/documents
# The final directory must not exist; its parent must exist:
./bin/Debug/net10.0/mframe-fs.exe mount C:\Mounts\Mainframe
```

Start the kernel and unlock containers using `mframe volume mount` first. An export
of `/` shows `M:\vol\documents`; locked volumes are listed but cannot be opened.
`--root` defaults to `/`. `--state DIR` and `--endpoint HOST:PORT` use the same
local credentials and loopback endpoint conventions as `mframe`. Run as the same
user and elevation level as the applications that will access the drive. Mount
points and synthetic security descriptors restrict access to that user and SYSTEM.
ACL editing is unsupported; Mainframe volume grants remain authoritative.
Directory mounts beneath `%TEMP%` are rejected: on the qualified Windows system,
directory creation there falsely succeeds without reaching the filesystem, also
reproduced using the official WinFsp sample. Use a drive letter or a directory
outside `%TEMP%`, such as `C:\Mounts\Mainframe`. Spaces in mount paths are supported.

Keep the command running. Ctrl+C drains operations and unmounts. A disconnected
kernel causes unmount and a nonzero exit; restart the command to reconnect. Failed
mutations are never replayed. Unmount the Windows export before unmounting its
encrypted volume. Preserve container recovery sidecars after an unexpected stop.

The adapter supports file and directory creation, copying, seeking, truncation,
timestamps, supported attributes, rename/replacement, and deletion. Namespace and
volume roots are immutable. Transfers use bounded 64 KiB RPC chunks, so a large
write or append is not one atomic transaction. Use temporary-file replacement to
publish complete files. Alternate streams, links/reparse points, persistent ACLs,
compression, Windows sparse controls, and cross-volume rename are unsupported.

**Concurrent SDK access has limited guarantees.** Windows sharing and byte-range
locks coordinate applications on the same mount, but are not propagated to SDK
clients or other mounts. SDK locks are checked when Windows I/O reaches the
backend. Windows caching can delay visibility and conflict with concurrent SDK
edits. Close and reopen files to refresh; external SDK changes do not generate
Explorer notifications. Do not rely on this export for cross-client database
locking or simultaneous editing of the same file.

To publish only the adapter, with the client/protocol libraries:

```powershell
dotnet publish src/Mainframe.WinFsp -c Release -o artifacts/winfsp-publish
```

WinFsp remains an installed prerequisite. Build-time discovery can be overridden
with `-p:WinFspInstallDir="C:\path\to\WinFsp"`. The main solution's host tests also
require SQLCipher. On Visual Studio 2026 with MSVC 14.51.36231:

```powershell
./native/build.ps1 -VsInstall 'C:\Program Files\Microsoft Visual Studio\18\Community' -VcToolsVersion 14.51.36231
dotnet test tests/Mainframe.WinFsp.Tests -c Release
```

The separate mount suite requires the actual driver and fails explicitly if its
prerequisites are absent. It uses temporary kernel state, encrypted volumes, and
mounts. The adapter acceptance record reports 220 tests across the full solution
(186 existing tests and 34 adapter tests) in both Debug and Release. Manual
Explorer, Notepad, and VS Code UI workflows remain unverified. See
[the adapter acceptance record](plan.md#winfsp-adapter-september-26-2026).

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos.
[WinFsp project and license](https://github.com/winfsp/winfsp).
