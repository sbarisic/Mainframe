# Mainframe

A modular mainframe-style computer environment, starting with the `mf` CLI.
Requires the .NET 10 SDK (10.0.400 feature band).

See [plan.md](plan.md) for the proposed kernel, peer protocol, and implementation milestones.

## Run

```powershell
dotnet build Mainframe.slnx
dotnet run --project src/Mainframe.Cli -- help
dotnet run --project src/Mainframe.Cli -- status
dotnet run --project src/Mainframe.Cli -- version
```

To install a repository-local `mf` tool:

```powershell
dotnet pack src/Mainframe.Cli -c Release -o artifacts/packages
dotnet tool install Mainframe.Cli --version 0.1.0 --add-source artifacts/packages --tool-path artifacts/tools
./artifacts/tools/mf help
```

Alternatively, the build creates `src/Mainframe.Cli/bin/Debug/net10.0/mf.exe` on Windows.

## Commands

| Command | Purpose |
| --- | --- |
| `mf help [command]` | List commands or describe one command |
| `mf status` | Report which components are implemented |
| `mf version` | Print the CLI version |

No arguments shows help. `--help`, `-h`, `--version`, and per-command `--help` are supported.
Exit codes: `0` success, `1` operation failure, `2` invalid usage.
Errors go to stderr; normal output goes to stdout.

## Structure and scope

`src/Mainframe.Cli/Commands` contains individual `ICommand` implementations.
`CommandRouter` handles lookup, help, and argument validation. Register new commands
in `Program.cs`. Extend the command argument contract when commands need operands.

This version is a CLI foundation. It has no daemon, kernel, persistent state,
filesystem commands, or Windows filesystem adapter. Status describes implementation
availability; it does not probe a running service.

The next layer will provide a kernel API shared by CLI commands and, eventually,
a Windows filesystem adapter. Keep storage and runtime behavior behind that API
instead of implementing it in command parsing or launching CLI subprocesses from
the filesystem adapter.

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
