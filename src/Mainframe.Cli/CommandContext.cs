using System.Net;
using System.Text.Json;

namespace Mainframe.Cli;

public sealed class CommandContext(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> options, TextWriter output)
{
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public TextWriter Output { get; } = output;
    public bool Json => options.ContainsKey("--json");
    public bool Terminal => options.ContainsKey("--terminal");
    public string StateDirectory => Path.GetFullPath(GetOption("--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mainframe"));

    public string? GetOption(string name) => options.GetValueOrDefault(name);
    public void Validate(string usage, params string[] allowedOptions)
    {
        foreach (var option in options.Keys)
            if (!allowedOptions.Contains(option, StringComparer.Ordinal))
                throw new UsageException($"Option '{option}' is not supported. Usage: {usage}");
    }

    public void RequireNoArguments(string usage)
    {
        if (Arguments.Count != 0)
            throw new UsageException($"Unexpected arguments. Usage: {usage}");
    }

    public KernelEndpoint GetEndpoint()
    {
        var value = GetOption("--endpoint") ?? "localhost:7443";
        if (!Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out Uri? uri) || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new UsageException("Endpoint must be a host and port, for example localhost:7443.");
        var host = uri.DnsSafeHost;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && (!IPAddress.TryParse(host, out IPAddress? address) || !IPAddress.IsLoopback(address)))
            throw new UsageException("This first kernel milestone accepts loopback endpoints only (localhost or a loopback IP address).");
        return new KernelEndpoint(host, uri.Port, value);
    }

    public void WriteJson<T>(T value) => Output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed record KernelEndpoint(string Host, int Port, string Display);
public sealed class UsageException(string message) : Exception(message);
