using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "DefaultConnection";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved when the context is first created, so hosts and tests can supply configuration late.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured. Set it in appsettings.Development.json " +
                    "for development, or via the ConnectionStrings__DefaultConnection environment variable or user secrets.");
            }

            options.UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
        });

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();

        services.AddAppIdentity();

        return services;
    }

    /// <summary>ASP.NET Core Identity with the M2 policy: email username, strong passwords, lockout, active-user checks.</summary>
    private static void AddAppIdentity(this IServiceCollection services)
    {
        services
            .AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 10;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
                options.SignIn.RequireConfirmedPhoneNumber = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<AppSignInManager>()
            .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>();

        // Cookie validation re-checks the security stamp and IsActive at this interval.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
        services.AddScoped<ISecurityStampValidator, ActiveUserSecurityStampValidator>();

        services.AddSingleton<ITemporaryPasswordGenerator, TemporaryPasswordGenerator>();
        services.AddScoped<AdminSeeder>();
        services.AddScoped<ManagerService>();
        services.AddScoped<AccountService>();

        services.AddScoped<People.PersonService>();
        services.AddScoped<People.DemoDataSeeder>();

        services.AddScoped<Rates.ExchangeRateService>();
        services.AddScoped<Rates.IExchangeRateService>(sp => sp.GetRequiredService<Rates.ExchangeRateService>());

        services.AddScoped<HR.Domain.Pay.IPayrollLock, Payroll.PayrollLock>();
        services.AddScoped<Payroll.PayrollService>();
        services.AddScoped<Settings.SettingsService>();
        services.AddScoped<Invoices.InvoiceService>();
        services.AddScoped<Payroll.OwnerIncomeService>();
        services.AddScoped<Pay.PayRecordService>();
        services.AddScoped<Pay.SalaryOverviewService>();
        services.AddScoped<Absences.AbsenceService>();
        services.AddScoped<Reports.ReportService>();

        // PDF exports: Community licence and the embedded font, registered once per process.
        Exports.PdfSetup.Configure();
    }
}
