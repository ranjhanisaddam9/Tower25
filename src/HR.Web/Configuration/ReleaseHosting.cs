using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace HR.Web.Configuration;

/// <summary>
/// Server deployment (M10, IIS): the machine configuration lives in a folder outside the web root, next to the app
/// folder, so updates never touch it and IIS can never serve it.
/// </summary>
public static class ReleaseHosting
{
    public const string ConfigDirVariable = "HRPAYROLL_CONFIG_DIR";

    /// <summary>
    /// The server's config folder: <c>HRPAYROLL_CONFIG_DIR</c> if set, else <c>..\config</c> next to the app folder
    /// (<c>C:\HRPayroll\app</c> → <c>C:\HRPayroll\config</c>).
    /// </summary>
    public static string ConfigDirectory() =>
        Environment.GetEnvironmentVariable(ConfigDirVariable) is { Length: > 0 } dir
            ? dir
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config"));

    /// <summary>
    /// Outside Development, adds <c>{config folder}\appsettings.{Environment}.json</c> (optional) right after the
    /// app's own appsettings files, so environment variables and the command line still override it.
    /// </summary>
    public static void AddInstallConfigFile(this IConfigurationBuilder configuration, IHostEnvironment environment, string? directory = null)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        directory ??= ConfigDirectory();
        if (!Directory.Exists(directory))
        {
            return;
        }

        var source = new JsonConfigurationSource
        {
            Path = $"appsettings.{environment.EnvironmentName}.json",
            Optional = true,
            ReloadOnChange = false,
            FileProvider = new PhysicalFileProvider(directory),
        };

        var sources = configuration.Sources;
        var lastAppSettings = sources
            .Select((s, i) => (s, i))
            .Where(x => x.s is JsonConfigurationSource json && json.Path?.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) == true)
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .Max();
        sources.Insert(lastAppSettings + 1, source);
    }
}
