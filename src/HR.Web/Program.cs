using System.Text.Encodings.Web;
using System.Text.Unicode;
using HR.Infrastructure;
using HR.Infrastructure.Data;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Infrastructure.Security;
using HR.Web.Configuration;
using HR.Web.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows Service the working directory is System32: views and wwwroot live next to the exe.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// Release package (M10): the Windows Service "HRPayroll" (a no-op when run from a console or by dotnet run).
builder.Host.UseWindowsService(options => options.ServiceName = ReleaseHosting.ServiceName);

// Optional, git-ignored machine overrides (e.g. this PC's SQL Server name), right after appsettings.{env}.json.
builder.Configuration.AddLocalSettingsFile(builder.Environment);

// Installed copies: C:\HRPayroll\config\appsettings.Production.json (outside the app folder; updates never touch it).
builder.Configuration.AddInstallConfigFile(builder.Environment);
builder.UseStoreCertificate();

// Structured logs (M10): a rolling daily JSON file (30 kept). JSON escapes CR/LF, so user text can never forge a log line.
// Serilog is one more logging provider: the console and any other provider keep receiving the same events unchanged.
var fileLog = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext();
if (builder.Configuration["Logging:File:Path"] is { Length: > 0 } logPath)
{
    fileLog.WriteTo.File(new CompactJsonFormatter(), logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, shared: true);
}

var serilog = fileLog.CreateLogger();
builder.Logging.AddSerilog(serilog, dispose: true);
builder.Services.AddSingleton(new Serilog.Extensions.Hosting.DiagnosticContext(serilog));
builder.Services.AddSingleton<IDiagnosticContext>(sp => sp.GetRequiredService<Serilog.Extensions.Hosting.DiagnosticContext>());

builder.AddRequestLimits();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAuditContext, HttpAuditContext>();
builder.Services.AddAppDataProtection();
builder.Services.AddAppHealthChecks();

builder.Services.AddMemoryCache(); // short-lived payroll recalculation diffs (too big for the TempData cookie)
builder.Services.AddSingleton<HR.Web.Exports.Downloads>(); // M9: export responses and their audit events
builder.Services.AddControllersWithViews(options =>
{
    // Every unsafe HTTP method (POST/PUT/PATCH/DELETE) must carry a valid antiforgery token.
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

// Emit non-ASCII text (–, —, ·, Urdu names) as-is; HTML-significant characters are still encoded.
builder.Services.Configure<WebEncoderOptions>(options =>
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddAppAuthCookie();
builder.Services.AddAppAuthorization();
builder.Services.AddSecureCookies();
builder.Services.AddLoginRateLimit(builder.Configuration);

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

var app = builder.Build();

// Server console commands (admin-reset, audit-purge): run and exit. Never reachable over HTTP.
if (args.Length > 0 && ServerCommands.Names.Contains(args[0]))
{
    await using var scope = app.Services.CreateAsyncScope();
    Environment.ExitCode = await scope.ServiceProvider.GetRequiredService<ServerCommands>().RunAsync(args, Console.Out);
    return;
}

await StartupTasks.RunAsync(app);

app.UseMiddleware<SecurityHeadersMiddleware>();

// Development keeps the framework's developer exception page (added automatically); never anywhere else.
if (!app.Environment.IsDevelopment())
{
    // Generic error page only: no stack traces or exception text; a correlation id to quote. Database outages are 503.
    app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        ExceptionHandlingPath = "/error/500",
        StatusCodeSelector = exception => Hardening.IsDatabaseUnavailable(exception) ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
    });
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseHttpsRedirection();
app.UseSerilogRequestLogging(options =>
{
    options.Logger = serilog; // file only
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms"; // path only, never the query string
});
app.UseCookiePolicy();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter(); // after authentication: limits are per signed-in user
app.UseAuthorization();
app.UseMiddleware<ForcePasswordChangeMiddleware>();
app.UseMiddleware<RequireTwoFactorMiddleware>();

app.MapStaticAssets().AllowAnonymous();
app.MapAppHealth();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

await app.RunAsync();

/// <summary>Startup work that must never take the site down: a database outage is logged and the site answers 503.</summary>
internal static class StartupTasks
{
    public static async Task RunAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        try
        {
            // Migrations at startup are a development convenience only: production uses the migration bundle
            // (migrate.exe, see INSTALL.md), so the setting is ignored there even if set.
            if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
            {
                if (app.Environment.IsDevelopment())
                {
                    await DatabaseMigrator.MigrateAsync(app.Services);
                }
                else
                {
                    log.LogWarning("Database:MigrateOnStartup is ignored outside Development; apply migrations with the migration bundle.");
                }
            }

            // Roles always; the first Admin only when none exists and Seed:Admin:* is configured.
            await AdminSeeder.RunAsync(app.Services);

            // Development only, and only when DemoData:Seed is true (never committed as true).
            if (app.Environment.IsDevelopment())
            {
                await DemoDataSeeder.RunIfEnabledAsync(app.Services, app.Configuration);
            }
        }
        catch (Exception ex) when (Hardening.IsDatabaseUnavailable(ex))
        {
            log.LogCritical("The database is unavailable at startup; seeding skipped. Requests that need it will get 503 until it is back.");
        }
    }
}
