# Mainframe

A modular mainframe-style computer environment with a local authenticated kernel.
Requires the .NET 10 SDK (10.0.400 feature band).

See [plan.md](plan.md) for the architecture and milestones, and [packets.md](packets.md)
for the wire protocol. The current execution milestone is tested on Windows only.

## Run

```powershell
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
eight channels per exchange, and 4,096 lifetime exchange records. Streaming uses
64 KiB receive windows within a bounded connection budget. Queries and launches
have a ten-second client deadline; program runtime does not inherit that deadline.

Disconnect detection initiates foreground cleanup, with up to two seconds for
graceful termination before the Job Object is terminated. Host shutdown permits
a ten-second transport drain. Startup marks unfinished execution records
`outcome_unknown`; it never retries them. There are no durable background jobs yet.

State schema 1 migrates transactionally to schema 2. Newer schemas are rejected.
The initial local operator has an explicit persisted wildcard grant. Program RPCs
are further restricted to declared kernel-query permissions. Multi-user grant
administration, network enrollment/automatic renewal, peer delegation, Linux PTYs,
filesystems, and public endpoint hardening remain later work.

## Next milestone: encrypted local storage (planned)

The next implementation adds password-unlocked binary containers mounted at
`/vol/<name>`. It is documented in [plan.md](plan.md#next-milestone-local-encrypted-volumes).
No volume commands, filesystem SDK methods, or encryption provider exist yet.
Current `/host/<name>` entries only select host working directories.

Each `.mfv` will contain encrypted filenames, metadata, and 64 KiB content chunks,
using a validated SQLCipher native build. Programs will access files through the
SDK; this does not mount a Windows drive or change ordinary `System.IO` paths.

Proposed commands, for after implementation:

```powershell
mframe volume create E:\Mainframe\Data\documents.mfv
mframe volume mount E:\Mainframe\Data\documents.mfv /vol/documents
mframe volume list
mframe volume unmount /vol/documents
```

Create/mount prompt for passwords; no password argument or saved unlock secret is
planned. Creation leaves the volume unmounted. Configured mounts restart locked.
There is no password recovery or automatic unlock. Unmount requires no active file
handles and leaves host data intact. Copy a container only after successful clean
unmount; WAL/SHM recovery sidecars are allowed while live and must survive crashes.

The milestone includes bounded file RPCs, SDK streams, volume permissions,
transactional writes, atomic same-volume replacement, and Windows crash-recovery
acceptance. Separate `ls`, `cat`, and storage-demo programs will exercise the SDK.
Host-directory storage, remote volumes, Linux acceptance, saved keys/rekeying,
live backup tooling, and WinFsp follow later. The encryption dependency and native
packaging still require validation; Mainframe remains MIT with dependency notices.

## Verification

```powershell
dotnet test Mainframe.slnx -c Release
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

WinFsp is a candidate for the future Windows filesystem adapter; it is not a
dependency of the current CLI. Its [GPLv3 license with a FLOSS exception](https://github.com/winfsp/winfsp/blob/master/License.txt)
allows qualifying open-source applications to link to its specified DLLs without
adopting GPLv3. The exception requires attribution in the user interface and
user-facing documentation, and prohibits linking or distributing the software
with proprietary software. It also permits redistribution of unmodified official
WinFsp installers. WinFsp itself retains its own license. Review the selected
release's terms when implementing and distributing the adapter.
