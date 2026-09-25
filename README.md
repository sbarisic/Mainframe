# Mainframe

A modular mainframe-style computer environment with a local authenticated kernel.
Requires the .NET 10 SDK (10.0.400 feature band).

See [plan.md](plan.md) for the architecture and milestones, and [packets.md](packets.md)
for the wire protocol. This first runtime milestone is tested on Windows only.

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

The initial leaf certificates expire after seven days. Renewal and invitation-based
enrollment are not implemented yet; expired certificates fail explicitly. Preserve
existing state rather than deleting it to bypass initialization checks. A fresh
test state directory creates a different mainframe identity.

To package the CLI as a repository-local `mf` tool:

```powershell
dotnet pack src/Mainframe.Cli -c Release -o artifacts/packages
dotnet tool install Mainframe.Cli --version 0.1.0 --add-source artifacts/packages --tool-path artifacts/tools
./artifacts/tools/mf help
```

Alternatively, the build creates `src/Mainframe.Cli/bin/Debug/net10.0/mf.exe` on Windows.
The host executable is `src/Mainframe.Host/bin/Debug/net10.0/mfd.exe`.

## Commands

| Command | Purpose |
| --- | --- |
| `mf help [command]` | List commands or describe one command |
| `mf cluster init` | Create private local kernel state and initial credentials |
| `mf status` | Query the running kernel's identity and health |
| `mf health` | Query readiness, uptime, and connections |
| `mf capabilities` | List the kernel's implemented syscall contracts |
| `mf version` | Print the CLI version |

No arguments shows help. `--help`, `-h`, `--version`, and per-command `--help` are supported.
Query commands support `--state`, `--endpoint`, and `--json` before or after the
command. Endpoints are restricted to loopback for this milestone.
Exit codes: `0` success, `1` operation failure, `2` invalid usage, `130` cancelled.
Errors go to stderr; normal output goes to stdout.

## Structure and scope

`src/Mainframe.Cli/Commands` contains individual `ICommand` implementations.
`CommandRouter` handles async dispatch, help, arguments, cancellation, and errors.
Register new commands in `Program.cs`.

| Component | Implemented responsibility |
| --- | --- |
| Mainframe.Protocol | Bounded 20-byte frames, strict JSON and explicit wire contracts |
| Mainframe.Core | SQLite identity/authorization, private certificates, metadata backup API |
| Mainframe.Host | Loopback TLS host and explicit syscall dispatcher |
| Mainframe.Client | Authenticated unary RPC, deadlines and heartbeat |
| Mainframe.Cli | Local initialization and live kernel queries |

The host advertises only `kernel.describe`, `kernel.health`, and
`kernel.capabilities`. Unsupported methods fail explicitly. It bounds connections
to 32 and simultaneous TLS handshakes to 8; initial unary sessions accept up to
4,096 monotonically increasing odd request IDs before GOAWAY. Requests are serviced
serially per connection, with no replay. Certificate authorization is rechecked on
each frame and idle check. SQLite databases stay on local storage.

Network enrollment/renewal, resource grants and delegated leases, peers, process
execution, terminal streams, volumes, and Windows mounts remain later milestones.
The presence of future frame types in the enum does not mean those operations are
accepted. Do not expose this development milestone as a public service. Linux has
not been tested yet.

## Verification

```powershell
dotnet test Mainframe.slnx -c Release
```

Tests cover framing/JSON validation, private state, persistence, certificate trust,
revocation, and real loopback TLS connections. Test fixtures use isolated temporary
state, not the default installation. See `docs/protocol` for the implemented wire
schema and fixture.

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
