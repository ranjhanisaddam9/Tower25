namespace HR.Infrastructure.Data;

/// <summary>The "Database" configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending migrations at startup. True only in appsettings.Development.json.</summary>
    public bool MigrateOnStartup { get; set; }
}
