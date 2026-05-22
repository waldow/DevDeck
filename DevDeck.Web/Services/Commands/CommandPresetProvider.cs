namespace DevDeck.Web.Services.Commands;

public sealed class CommandPresetProvider
{
    private readonly CommandExecutableResolver _resolver;

    public CommandPresetProvider(CommandExecutableResolver resolver)
    {
        _resolver = resolver;
    }

    public IReadOnlyList<CommandPreset> All()
    {
        return
        [
            new CommandPreset(
                Key: "AzureFunction",
                DisplayName: "Azure Function",
                StartCommand: _resolver.Resolve("func"),
                StartArguments: "start --port {port}",
                DefaultPort: 7071,
                UrlTemplate: "http://localhost:{port}",
                HealthCheckUrlTemplate: "http://localhost:{port}",
                DefaultEnvironment:
                [
                    new KeyValuePair<string, string>("AzureWebJobsStorage", "UseDevelopmentStorage=true")
                ]
            ),
            new CommandPreset(
                Key: "ReactVite",
                DisplayName: "React / Vite",
                StartCommand: _resolver.Resolve("npm"),
                StartArguments: "run dev -- --host 0.0.0.0 --port {port}",
                DefaultPort: 5173,
                UrlTemplate: "http://localhost:{port}",
                HealthCheckUrlTemplate: "http://localhost:{port}"
            ),
            new CommandPreset(
                Key: "ReactCra",
                DisplayName: "React (Create React App)",
                StartCommand: _resolver.Resolve("npm"),
                StartArguments: "start",
                DefaultPort: 3000,
                UrlTemplate: "http://localhost:{port}",
                HealthCheckUrlTemplate: "http://localhost:{port}",
                DefaultEnvironment: new[] { new KeyValuePair<string, string>("PORT", "{port}") }
            ),
            new CommandPreset(
                Key: "NodeApi",
                DisplayName: "Node API",
                StartCommand: _resolver.Resolve("npm"),
                StartArguments: "run dev",
                DefaultPort: 3001,
                UrlTemplate: "http://localhost:{port}",
                HealthCheckUrlTemplate: "http://localhost:{port}/health"
            ),
            new CommandPreset(
                Key: "DotNetApi",
                DisplayName: ".NET API",
                StartCommand: _resolver.Resolve("dotnet"),
                StartArguments: "run --urls http://localhost:{port}",
                DefaultPort: 5080,
                UrlTemplate: "http://localhost:{port}",
                HealthCheckUrlTemplate: "http://localhost:{port}/health"
            ),
            new CommandPreset(
                Key: "DockerCompose",
                DisplayName: "Docker Compose",
                StartCommand: _resolver.Resolve("docker"),
                StartArguments: "compose up",
                DefaultPort: null,
                UrlTemplate: null,
                HealthCheckUrlTemplate: null
            ),
            new CommandPreset(
                Key: "Custom",
                DisplayName: "Custom",
                StartCommand: "",
                StartArguments: "",
                DefaultPort: null,
                UrlTemplate: null,
                HealthCheckUrlTemplate: null
            ),
        ];
    }

    public CommandPreset? Find(string key) =>
        All().FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

public sealed record CommandPreset(
    string Key,
    string DisplayName,
    string StartCommand,
    string StartArguments,
    int? DefaultPort,
    string? UrlTemplate,
    string? HealthCheckUrlTemplate,
    IReadOnlyCollection<KeyValuePair<string, string>>? DefaultEnvironment = null);
