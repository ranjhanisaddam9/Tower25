using System.Globalization;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.People;

/// <summary>
/// Development-only demo people (DemoData:Seed = true; never committed as true). Idempotent: people are matched by
/// their demo email, so re-running only adds what is missing. CNICs start with 00000 and IBANs use the bank code
/// TEST, so they are obviously fake; phones are +92 300 0000xxx.
/// </summary>
public sealed class DemoDataSeeder(AppDbContext db, IHostEnvironment environment, IClock clock, ILoggerFactory loggerFactory)
{
    public const string FlagKey = "DemoData:Seed";
    public const string ActorId = "demo-data-seed";
    public const string EmailDomain = "@demo.example";

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    private static readonly DemoPerson[] People =
    [
        new("Imran Qureshi", PersonType.Employee, "Engagement Lead", "2024-03-04", null, HireSource.Owner),
        new("Ayesha Siddiqui", PersonType.Employee, "Senior Software Engineer", "2024-05-13", null, HireSource.CompanyRecommended),
        new("Bilal Ahmed", PersonType.Employee, "QA Engineer", "2024-08-01", null, HireSource.BudgetHire),
        new("Hira Malik", PersonType.Internee, "Design Intern", "2026-06-01", null, HireSource.BudgetHire),
        new("Usman Tariq", PersonType.Employee, "Support Lead", "2023-11-06", "2026-08-29", HireSource.CompanyRecommended),
        new("Fatima Zahra", PersonType.Employee, "Data Analyst", "2025-01-06", null, HireSource.CompanyRecommended),
        new("Hamza Sheikh", PersonType.Employee, "DevOps Engineer", "2024-10-14", null, HireSource.BudgetHire),
        new("Zainab Hussain", PersonType.Internee, "Software Intern", "2026-07-01", null, null),
        new("Omar Farooq", PersonType.Employee, "Backend Engineer", "2025-02-03", null, HireSource.CompanyRecommended),
        new("Mariam Javed", PersonType.Employee, "Product Designer", "2024-12-02", null, HireSource.BudgetHire),
        new("Ali Raza", PersonType.Employee, "Frontend Engineer", "2025-04-01", null, HireSource.CompanyRecommended),
        new("Sana Khalid", PersonType.Internee, "QA Intern", "2026-08-03", null, null),
        new("Ahmed Nawaz", PersonType.Employee, "Mobile Engineer", "2024-06-17", "2026-03-31", HireSource.BudgetHire),
        new("Mahnoor Iqbal", PersonType.Employee, "Business Analyst", "2025-07-01", null, HireSource.CompanyRecommended),
        new("Danish Mehmood", PersonType.Employee, "Systems Administrator", "2023-09-04", null, HireSource.BudgetHire),
        new("Iqra Shahid", PersonType.Internee, "Data Intern", "2026-09-01", null, HireSource.BudgetHire),
        new("Faisal Rehman", PersonType.Employee, "Technical Writer", "2025-03-17", null, null),
        new("Amna Rashid", PersonType.Employee, "HR Coordinator", "2024-01-15", null, HireSource.CompanyRecommended),
        new("Kashif Aziz", PersonType.Employee, "Network Engineer", "2024-04-01", "2025-12-31", HireSource.CompanyRecommended),
        new("Rabia Anwar", PersonType.Internee, "Marketing Intern", "2026-05-04", "2026-08-31", HireSource.BudgetHire),
        new("Saad Akhtar", PersonType.Employee, "Full-Stack Engineer", "2025-09-01", null, HireSource.BudgetHire),
        new("Noor Fatima", PersonType.Employee, "UX Researcher", "2025-10-01", null, null),
        new("Waqas Butt", PersonType.Employee, "Database Administrator", "2024-02-05", null, HireSource.CompanyRecommended),
        new("Laiba Saeed", PersonType.Internee, "Support Intern", "2026-09-16", null, null),
        new("Tariq Mahmood", PersonType.Employee, "Project Coordinator", "2025-05-05", null, HireSource.BudgetHire),
    ];

    public static async Task RunIfEnabledAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>(FlagKey))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(cancellationToken);
    }

    /// <summary>Adds any missing demo people. Does nothing outside the Development environment. Returns the number added.</summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            return 0;
        }

        var existing = await db.People.AsNoTracking()
            .Where(p => p.Email != null && p.Email.EndsWith(EmailDomain))
            .Select(p => p.Email!)
            .ToListAsync(cancellationToken);
        var existingEmails = existing.ToHashSet(StringComparer.Ordinal);
        var hasActiveOwner = await db.People.AnyAsync(p => p.IsActive && p.HireSource == HireSource.Owner, cancellationToken);

        var added = 0;
        for (var i = 0; i < People.Length; i++)
        {
            var demo = People[i];
            var email = EmailFor(demo.FullName);
            if (existingEmails.Contains(email))
            {
                continue;
            }

            var n = i + 1;
            var input = new PersonInput(
                demo.FullName,
                demo.Type,
                demo.Designation,
                email,
                $"+9230000{n:00000}",
                $"00000-{n:0000000}-{n % 10}",
                "Demo Bank (fake)",
                PakistaniIban.Create("TEST", n.ToString("0000000000000000", CultureInfo.InvariantCulture)),
                DateOnly.Parse(demo.Joined, CultureInfo.InvariantCulture),
                "Demo data: not a real person.");

            var now = clock.UtcNow;
            var person = Person.Create(await db.NextPersonCodeNumberAsync(cancellationToken), input, ActorId, now);

            var source = demo.Source;
            if (source == HireSource.Owner && (hasActiveOwner || demo.Left is not null))
            {
                source = null; // never create a second active Owner
            }

            person.SetHireSource(source, ActorId, now);
            if (demo.Left is { } left)
            {
                person.Deactivate(DateOnly.Parse(left, CultureInfo.InvariantCulture), ActorId, now);
            }

            hasActiveOwner |= source == HireSource.Owner;
            db.People.Add(person);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            SecurityLog.DemoDataSeeded(_log, added);
        }

        return added;
    }

    private static string EmailFor(string fullName) =>
        fullName.ToLowerInvariant().Replace(' ', '.') + EmailDomain;

    private sealed record DemoPerson(string FullName, PersonType Type, string Designation, string Joined, string? Left, HireSource? Source);
}
