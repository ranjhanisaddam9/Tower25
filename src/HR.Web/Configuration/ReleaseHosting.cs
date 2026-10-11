using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace HR.Web.Configuration;

/// <summary>
/// The portable release package (M10): runs as the Windows Service "HRPayroll" or as a console app, reads its machine
/// configuration from a folder outside the app folder (so updates never touch it), and serves HTTPS with a certificate
/// from the LocalMachine\My store, chosen by thumbprint.
/// </summary>
public static class ReleaseHosting
{
    public const string ServiceName = "HRPayroll";
    public const string ConfigDirVariable = "HRPAYROLL_CONFIG_DIR";
    public const string ThumbprintKey = "HttpsCertificate:Thumbprint";

    /// <summary>
    /// The install's config folder: <c>HRPAYROLL_CONFIG_DIR</c> if set, else <c>..\config</c> next to the app folder
    /// (<c>C:\HRPayroll\app</c> → <c>C:\HRPayroll\config</c>; also right for <c>app.previous</c> after a rollback).
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

    /// <summary>When <c>HttpsCertificate:Thumbprint</c> is set, every HTTPS endpoint uses that LocalMachine\My certificate.</summary>
    public static void UseStoreCertificate(this WebApplicationBuilder builder)
    {
        var thumbprint = builder.Configuration[ThumbprintKey];
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return;
        }

        var certificate = FindCertificate(thumbprint);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var clean = new string(thumbprint.Where(char.IsAsciiHexDigit).ToArray());
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, clean, validOnly: false);
        return found.Count > 0
            ? found[0]
            : throw new InvalidOperationException($"The HTTPS certificate {clean} is not in LocalMachine\\My. Re-run setup.ps1 to create it.");
    }
}
