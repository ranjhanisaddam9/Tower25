using HR.Infrastructure.Backups;
using HR.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace HR.Tests.Unit;

/// <summary>M10 release package: the backup warning rules and the install config file.</summary>
public class ReleasePackageUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_status_file_means_no_backup_has_run()
    {
        Assert.Equal(BackupProblem.NeverRun, BackupStatusService.Evaluate(null, Now)!.Problem);
    }

    [Fact]
    public void A_recent_good_backup_gives_no_warning()
    {
        Assert.Null(BackupStatusService.Evaluate(new BackupStatusFile(Now.AddHours(-1), true, Now.AddHours(-1), "OK"), Now));
        Assert.Null(BackupStatusService.Evaluate(new BackupStatusFile(Now.AddHours(-48), true, Now.AddHours(-48), null), Now));
    }

    [Fact]
    public void A_good_backup_older_than_48_hours_is_stale()
    {
        var warning = BackupStatusService.Evaluate(new BackupStatusFile(Now.AddHours(-49), true, Now.AddHours(-49), null), Now);
        Assert.Equal(BackupProblem.Stale, warning!.Problem);
        Assert.Equal(Now.AddHours(-49), warning.LastGoodUtc);
    }

    [Fact]
    public void A_failed_last_run_warns_even_when_an_older_backup_is_recent_and_keeps_the_message_short()
    {
        var warning = BackupStatusService.Evaluate(new BackupStatusFile(Now, false, Now.AddHours(-2), new string('x', 500)), Now);
        Assert.Equal(BackupProblem.LastRunFailed, warning!.Problem);
        Assert.Equal(BackupStatusService.MessageMaxLength + 1, warning.Message!.Length);
    }

    [Fact]
    public void A_status_without_any_good_backup_is_stale()
    {
        Assert.Equal(BackupProblem.Stale, BackupStatusService.Evaluate(new BackupStatusFile(Now, true, null, null), Now)!.Problem);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "HR.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void The_install_config_file_overrides_appsettings_but_not_environment_variables()
    {
        var dir = Directory.CreateTempSubdirectory("hr-config-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.Production.json"), """{ "A": "install", "B": "install", "C": "install" }""");
            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "app", ["B"] = "app" })
                .AddJsonFile(new NullFileProvider(), "appsettings.json", optional: true, reloadOnChange: false)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["B"] = "environment variable" });
            builder.AddInstallConfigFile(new Env(Environments.Production), dir);
            var config = builder.Build();

            Assert.Equal("install", config["A"]);
            Assert.Equal("environment variable", config["B"]);
            Assert.Equal("install", config["C"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Development_never_reads_the_install_config_file()
    {
        var dir = Directory.CreateTempSubdirectory("hr-config-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.Development.json"), """{ "A": "install" }""");
            var builder = new ConfigurationBuilder();
            builder.AddInstallConfigFile(new Env(Environments.Development), dir);
            Assert.Null(builder.Build()["A"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
