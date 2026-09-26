using System.Text.Json;
using System.Text.Json.Serialization;
using Mainframe.Core;
using Mainframe.Protocol;

namespace Mainframe.Host;
/// <summary>Explicit registration table for the currently implemented kernel syscalls.</summary>
public sealed class KernelDispatcher
{
    private readonly Dictionary<string, Func<JsonElement>> handlers;
    public static readonly Capability[] Capabilities = [new("kernel.describe", 1, "Kernel and mainframe identity."), new("kernel.health", 1, "Live host health."), new("kernel.capabilities", 1, "Implemented syscall contracts."), new("program.register", 1, "Register an installed program manifest."), new("program.list", 1, "List registrations and compatibility."), new("program.remove", 1, "Remove a qualified registration."), new("host-root.add", 1, "Approve a named host working-directory root."), new("host-root.list", 1, "List approved host roots."), new("host-root.remove", 1, "Remove a host root."), new("shell.open", 1, "Open a connection-bound shell."), new("shell.command", 1, "Evaluate a simple shell command."), new("process.start", 1, "Launch a registered foreground process."), new("process.resize", 1, "Resize a running pseudoconsole."), new("process.interrupt", 1, "Send a console interrupt.")];
    public KernelDispatcher(KernelIdentity identity, DateTimeOffset startedAt, Func<int> connectionCount)
    {
        handlers = new(StringComparer.Ordinal)
        {
            ["kernel.describe"] = () => JsonSerializer.SerializeToElement(new KernelDescription(identity.MainframeId, identity.KernelId, identity.Name, "0.1.0", startedAt, Environment.OSVersion.ToString(), 1), HostJsonContext.Default.KernelDescription),
            ["kernel.health"] = () => JsonSerializer.SerializeToElement(new KernelHealth("ready", Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds), connectionCount()), HostJsonContext.Default.KernelHealth),
            ["kernel.capabilities"] = () => JsonSerializer.SerializeToElement(new KernelCapabilities(Capabilities, ExecutionFeatures.All), HostJsonContext.Default.KernelCapabilities)
        };
    }

    public RpcResponse Dispatch(RpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Method) || request.Version < 1 || request.TimeoutMs is <= 0 or > 30000)
            return Error("INVALID_ARGUMENT", "Invalid method, version, or timeout (1..30000 ms).");
        if (request.Version != 1 || !handlers.TryGetValue(request.Method, out Func<JsonElement>? handler))
            return Error("UNSUPPORTED_METHOD", "This kernel does not implement that method/version.");
        if (request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.EnumerateObject().Any())
            return Error("INVALID_ARGUMENT", "This method expects an empty arguments object.");
        return new RpcResponse(true, false, handler(), null);
    }

    public static RpcResponse Error(string code, string message) => new(false, false, null, new RpcError(code, message, "not_started"));
}

public sealed record Capability(string Method, int Version, string Description);
public sealed record KernelDescription(string MainframeId, string KernelId, string Name, string Version, DateTimeOffset StartedAt, string Os, int ProtocolVersion);
public sealed record KernelHealth(string Status, long UptimeSeconds, int ActiveConnections);
public sealed record KernelCapabilities(Capability[] Capabilities, string[] Features);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KernelDescription))]
[JsonSerializable(typeof(KernelHealth))]
[JsonSerializable(typeof(KernelCapabilities))]
internal partial class HostJsonContext : JsonSerializerContext;
