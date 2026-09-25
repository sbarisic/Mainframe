using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainframe.Protocol;

public sealed record HelloRequest(
    [property: JsonRequired] int[] Versions,
    [property: JsonRequired] string[] OptionalFeatures,
    [property: JsonRequired] string[] RequiredFeatures,
    [property: JsonRequired] string Role,
    [property: JsonRequired] string ClientName,
    [property: JsonRequired] int MaxFrameBytes);

public sealed record WelcomeResponse(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string[] Features,
    [property: JsonRequired] string KernelId,
    [property: JsonRequired] string MainframeId,
    [property: JsonRequired] int MaxFrameBytes,
    [property: JsonRequired] string Authentication);

public sealed record RpcRequest(
    [property: JsonRequired] string Method,
    [property: JsonRequired] int Version,
    int? TimeoutMs,
    [property: JsonRequired] JsonElement Arguments);

public sealed record RpcResponse(
    [property: JsonRequired] bool Ok,
    bool Streaming,
    JsonElement? Result,
    RpcError? Error);

public sealed record RpcError(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Message,
    [property: JsonRequired] string Outcome);

public sealed record GoAwayMessage([property: JsonRequired] string Reason, int? DrainTimeoutMs = null);

public static class ProtocolVersions
{
    public const int Current = 1;
    public const string UnaryRpcFeature = "unary-rpc";
}
