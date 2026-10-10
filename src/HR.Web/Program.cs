using System.Text.Encodings.Web;
using System.Text.Unicode;
using HR.Infrastructure;
using HR.Infrastructure.Data;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Web.Configuration;
using HR.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored machine overrides (e.g. this PC's SQL Server name), right after appsettings.{env}.json.
builder.Configuration.AddLocalSettingsFile(builder.Environment);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddMemoryCache(); // short-lived payroll recalculation diffs (too big for the TempData cookie)
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
builder.Services.AddLoginRateLimit();

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    await DatabaseMigrator.MigrateAsync(app.Services);
}

// Roles always; the first Admin only when none exists and Seed:Admin:* is configured.
await AdminSeeder.RunAsync(app.Services);

// Development only, and only when DemoData:Seed is true (never committed as true).
if (app.Environment.IsDevelopment())
{
    await DemoDataSeeder.RunIfEnabledAsync(app.Services, app.Configuration);
}

app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    // Generic error page only: no stack traces or exception text outside Development.
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseHttpsRedirection();
app.UseCookiePolicy();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter(); // after authentication: the change-password limit is per signed-in user
app.UseAuthorization();
app.UseMiddleware<ForcePasswordChangeMiddleware>();

app.MapStaticAssets().AllowAnonymous();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

await app.RunAsync();
