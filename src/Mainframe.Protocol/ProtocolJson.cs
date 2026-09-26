using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Mainframe.Protocol;
/// <summary>Strict portable JSON controls, using only registered serialization contracts.</summary>
public static class ProtocolJson
{
    public const int MaxDepth = 32;
    private static readonly ProtocolJsonContext Context = new(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = MaxDepth, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true });
    public static byte[] Serialize<T>(T value)
    {
        if (value is null)
        {
            return "null"u8.ToArray();
        }

        byte[] bytes = value is JsonNode node ? JsonSerializer.SerializeToUtf8Bytes(node, Context.JsonNode) : JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo(value.GetType()));
        if (bytes.Length > FrameCodec.MaxPayloadBytes)
        {
            throw new ProtocolException("JSON payload exceeds the frame limit.");
        }

        return bytes;
    }

    public static T Deserialize<T>(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateJson(payload);
        try
        {
            object? value = JsonSerializer.Deserialize(payload, GetTypeInfo(typeof(T)));
            if (value is not T typed)
            {
                throw new ProtocolException("A non-null JSON control object is required.");
            }

            ValidateContract(typed);
            return typed;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Invalid JSON control contract.", ex);
        }
    }

    public static JsonElement ToElement<T>(T value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                throw new ProtocolException("Undefined JSON is not a valid argument.");
            }

            return element.Clone();
        }

        byte[] bytes = Serialize(value);
        ValidateJson(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
        return document.RootElement.Clone();
    }

    public static void ValidateJson(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > FrameCodec.MaxPayloadBytes)
        {
            throw new ProtocolException("JSON payload exceeds the frame limit.");
        }

        try
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = MaxDepth });
            var objects = new Stack<HashSet<string>>();
            bool any = false;
            while (reader.Read())
            {
                any = true;
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (!objects.Peek().Add(reader.GetString()!))
                        {
                            throw new ProtocolException("Duplicate JSON property names are forbidden.");
                        }

                        break;
                }
            }

            if (!any || reader.CurrentDepth != 0)
            {
                throw new ProtocolException("A complete JSON value is required.");
            }
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Malformed JSON control payload.", ex);
        }
    }

    private static JsonTypeInfo GetTypeInfo(Type type) => Context.GetTypeInfo(type) ?? throw new NotSupportedException($"No explicit wire serialization contract is registered for {type.Name}. Supply JsonElement or JsonNode for application arguments.");
    private static void ValidateContract<T>(T value)
    {
        switch (value)
        {
            case HelloRequest hello:
                Require(hello.Versions is { Length: > 0 and <= 16 } && hello.Versions.All(v => v > 0), "Invalid version advertisement.");
                ValidateFeatures(hello.OptionalFeatures);
                ValidateFeatures(hello.RequiredFeatures);
                Require(hello.Role is "terminal" or "program" or "peer" or "filesystem", "Invalid connection role.");
                Require(!string.IsNullOrWhiteSpace(hello.ClientName) && hello.ClientName.Length <= 128, "Invalid client name.");
                Require(hello.MaxFrameBytes is > 0 and <= FrameCodec.MaxPayloadBytes, "Invalid frame limit.");
                break;
            case WelcomeResponse welcome:
                Require(welcome.Version > 0, "Invalid selected version.");
                ValidateFeatures(welcome.Features);
                Require(!string.IsNullOrWhiteSpace(welcome.KernelId) && welcome.KernelId.Length <= 256, "Invalid kernel identity.");
                Require(!string.IsNullOrWhiteSpace(welcome.MainframeId) && welcome.MainframeId.Length <= 256, "Invalid mainframe identity.");
                Require(welcome.MaxFrameBytes is > 0 and <= FrameCodec.MaxPayloadBytes, "Invalid frame limit.");
                Require(welcome.Authentication is "authenticated" or "required", "Invalid authentication state.");
                break;
            case RpcRequest request:
                Require(!string.IsNullOrWhiteSpace(request.Method) && request.Method.Length <= 128, "Invalid RPC method.");
                Require(request.Version > 0, "Invalid RPC method version.");
                Require(request.TimeoutMs is null or > 0, "Invalid request deadline.");
                Require(request.Arguments.ValueKind == JsonValueKind.Object, "RPC arguments must be a JSON object.");
                break;
            case RpcResponse response:
                Require(response.Ok ? response.Error is null && response.Result is not null : response.Error is not null && !response.Streaming && response.Result is null, "Inconsistent RPC response.");
                if (response.Error is { } responseError)
                {
                    ValidateContract(responseError);
                }

                break;
            case RpcError error:
                Require(!string.IsNullOrWhiteSpace(error.Code) && error.Code.Length <= 128, "Invalid RPC error code.");
                Require(error.Message is not null && error.Message.Length <= 4096, "Invalid RPC error message.");
                Require(error.Outcome is "not_started" or "completed" or "unknown", "Invalid RPC outcome.");
                break;
            case GoAwayMessage goAway:
                Require(!string.IsNullOrWhiteSpace(goAway.Reason) && goAway.Reason.Length <= 4096, "Invalid shutdown reason.");
                Require(goAway.DrainTimeoutMs is null or >= 0 and <= 10_000, "Invalid shutdown drain deadline.");
                break;
        }
    }

    private static void ValidateFeatures(string[]? features)
    {
        Require(features is { Length: <= 32 }, "Invalid feature list.");
        Require(features!.All(f => !string.IsNullOrWhiteSpace(f) && f.Length <= 128), "Invalid feature name.");
        Require(features!.Distinct(StringComparer.Ordinal).Count() == features!.Length, "Duplicate feature name.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ProtocolException(message);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(ProgramManifest))]
[JsonSerializable(typeof(ProgramRegistration))]
[JsonSerializable(typeof(ProgramRegistration[]))]
[JsonSerializable(typeof(RegisterProgramRequest))]
[JsonSerializable(typeof(SelectorRequest))]
[JsonSerializable(typeof(VolumeCreateRequest))]
[JsonSerializable(typeof(VolumeMountRequest))]
[JsonSerializable(typeof(VolumeInfo))]
[JsonSerializable(typeof(VolumeInfo[]))]
[JsonSerializable(typeof(VolumeCreated))]
[JsonSerializable(typeof(FsOpenV2))]
[JsonSerializable(typeof(FsOpenedV2))]
[JsonSerializable(typeof(FsEnumerate))]
[JsonSerializable(typeof(FsRenameHandle))]
[JsonSerializable(typeof(FsMetadata))]
[JsonSerializable(typeof(FsSize))]
[JsonSerializable(typeof(FsDisposition))]
[JsonSerializable(typeof(FsLock))]
[JsonSerializable(typeof(FsWriteV2))]
[JsonSerializable(typeof(FsCapabilities))]
[JsonSerializable(typeof(FsSpace))]
[JsonSerializable(typeof(FsDiscovery))]
[JsonSerializable(typeof(FsStat))]
[JsonSerializable(typeof(FsPath))]
[JsonSerializable(typeof(FsRename))]
[JsonSerializable(typeof(FsList))]
[JsonSerializable(typeof(FsEntry))]
[JsonSerializable(typeof(FsListing))]
[JsonSerializable(typeof(FsOpen))]
[JsonSerializable(typeof(FsOpened))]
[JsonSerializable(typeof(FsHandle))]
[JsonSerializable(typeof(FsRange))]
[JsonSerializable(typeof(FsTruncate))]
[JsonSerializable(typeof(StorageStarted))]
[JsonSerializable(typeof(FsTransferred))]
[JsonSerializable(typeof(FsCommitted))]
[JsonSerializable(typeof(HostRoot))]
[JsonSerializable(typeof(HostRoot[]))]
[JsonSerializable(typeof(ProcessStartRequest))]
[JsonSerializable(typeof(ProcessControlRequest))]
[JsonSerializable(typeof(ShellCommandRequest))]
[JsonSerializable(typeof(ShellState))]
[JsonSerializable(typeof(ProcessStarted))]
[JsonSerializable(typeof(ProcessExited))]
[JsonSerializable(typeof(WindowCredit))]
[JsonSerializable(typeof(BootstrapCredential))]
[JsonSerializable(typeof(BootstrapAuth))]
[JsonSerializable(typeof(AuthenticationResult))]
[JsonSerializable(typeof(EmptyArguments))]
[JsonSerializable(typeof(WelcomeResponse))]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcError))]
[JsonSerializable(typeof(GoAwayMessage))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
internal partial class ProtocolJsonContext : JsonSerializerContext;
