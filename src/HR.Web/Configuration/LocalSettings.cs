using Microsoft.Extensions.Configuration.Json;

namespace HR.Web.Configuration;

public static class LocalSettings
{
    /// <summary>
    /// Adds the optional, git-ignored <c>appsettings.{Environment}.local.json</c> directly after
    /// <c>appsettings.{Environment}.json</c>, so it overrides the committed file for this machine
    /// while user secrets, environment variables and the command line still win over it.
    /// </summary>
    public static void AddLocalSettingsFile(this ConfigurationManager configuration, IHostEnvironment environment)
    {
        var environmentFile = $"appsettings.{environment.EnvironmentName}.json";
        var localSource = new JsonConfigurationSource
        {
            Path = $"appsettings.{environment.EnvironmentName}.local.json",
            Optional = true,
            ReloadOnChange = true,
        };

        IConfigurationBuilder builder = configuration;
        var sources = builder.Sources;
        var index = sources
            .Select((source, i) => (source, i))
            .Where(x => x.source is JsonConfigurationSource json && string.Equals(json.Path, environmentFile, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .First();

        localSource.ResolveFileProvider();
        localSource.FileProvider ??= environment.ContentRootFileProvider;

        if (index < 0)
        {
            sources.Add(localSource);
        }
        else
        {
            sources.Insert(index + 1, localSource);
        }
    }
}
