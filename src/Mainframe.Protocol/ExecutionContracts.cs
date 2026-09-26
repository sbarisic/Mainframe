using System.Text.Json.Serialization;

namespace Mainframe.Protocol;

public sealed record ProgramManifest([property: JsonRequired] int ManifestVersion, [property: JsonRequired] string Identity, [property: JsonRequired] string Version, [property: JsonRequired] string Executable, [property: JsonRequired] string[] FixedArguments, [property: JsonRequired] string WorkingDirectory, [property: JsonRequired] string[] OperatingSystems, [property: JsonRequired] string[] Architectures, [property: JsonRequired] string[] IoModes, [property: JsonRequired] string[] EnvironmentAllowlist, [property: JsonRequired] string[] Permissions, [property: JsonRequired] string[] HostRoots, string? Interpreter = null, string? Runtime = null);
public sealed record ProgramRegistration([property: JsonRequired] string QualifiedName, [property: JsonRequired] string Revision, [property: JsonRequired] ProgramManifest Manifest, string? Incompatibility);
public sealed record RegisterProgramRequest([property: JsonRequired] ProgramManifest Manifest);
public sealed record SelectorRequest([property: JsonRequired] string Name);
public sealed record HostRoot([property: JsonRequired] string Name, [property: JsonRequired] string Path);
public sealed record ProcessStartRequest([property: JsonRequired] string Program, [property: JsonRequired] string[] Argv, string IoMode = "pipes", string? SessionId = null, int Columns = 80, int Rows = 25);
public sealed record ProcessControlRequest([property: JsonRequired] string ProcessId, string? Signal = null, int Columns = 80, int Rows = 25);
public sealed record ShellCommandRequest([property: JsonRequired] string SessionId, [property: JsonRequired] string Line, int Columns = 80, int Rows = 25);
public sealed record ShellState([property: JsonRequired] string SessionId, [property: JsonRequired] string WorkingDirectory, string? Text = null, bool Closed = false);
public sealed record StreamDescriptor([property: JsonRequired] uint Id, [property: JsonRequired] string Name, [property: JsonRequired] string Sender);
public sealed record ProcessStarted([property: JsonRequired] string ProcessId, [property: JsonRequired] StreamDescriptor[] Channels);
public sealed record ProcessExited([property: JsonRequired] int ExitCode, bool Cancelled = false);
public sealed record WindowCredit([property: JsonRequired, JsonPropertyName("bytes")] int Credit);
public sealed record BootstrapCredential([property: JsonRequired] string Token, [property: JsonRequired] string Host, [property: JsonRequired] int Port, [property: JsonRequired] string CaCertificate);
public sealed record BootstrapAuth([property: JsonRequired] string Token);
public sealed record AuthenticationResult([property: JsonRequired] bool Ok);
public sealed record EmptyArguments;
public static class ExecutionFeatures
{
    public static readonly string[] All = ["unary-rpc", "streaming-v1", "execution-v1", "shell-v1"];
}
