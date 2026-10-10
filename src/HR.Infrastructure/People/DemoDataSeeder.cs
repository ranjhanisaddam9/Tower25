using System.Globalization;
using HR.Domain.Absences;
using HR.Domain.Payroll;
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
/// Development-only demo people, pay and absences (DemoData:Seed = true; never committed as true). Idempotent: people are matched by
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

        // SPEC §9 look-alikes (M6): G3 (absences Oct 5 half, Oct 7 full), G2 (joins Thu Oct 8) and G7 (leaves Wed Oct 21).
        new("Kamran Yousaf", PersonType.Employee, "Solutions Engineer", "2025-06-02", null, HireSource.CompanyRecommended),
        new("Nadia Haider", PersonType.Employee, "Software Engineer", "2026-10-08", null, HireSource.CompanyRecommended),
        new("Rizwan Ali", PersonType.Employee, "Integration Engineer", "2025-06-16", "2026-10-21", HireSource.CompanyRecommended),
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

        await SeedRateRecordsAsync(cancellationToken);
        await SeedAbsencesAsync(cancellationToken);
        return added;
    }

    /// <summary>People whose demo absences are fixed by SPEC §9; nobody else on this list gets random ones.</summary>
    private static readonly (string FullName, (string Date, AbsencePortion Portion)[] Absences)[] GoldenAbsences =
    [
        ("Ayesha Siddiqui", []), // G1: none
        ("Imran Qureshi", []), // G6: none
        ("Nadia Haider", []), // G2: none
        ("Rizwan Ali", []), // G7: none
        ("Kamran Yousaf", [("2026-10-05", AbsencePortion.Half), ("2026-10-07", AbsencePortion.Full)]), // G3
        ("Bilal Ahmed", [("2026-10-06", AbsencePortion.Full), ("2026-10-20", AbsencePortion.Full), ("2026-10-27", AbsencePortion.Full)]), // G4/G5
    ];

    /// <summary>
    /// The SPEC §9 absences plus a few pseudo-random ones (fixed seed, so every run picks the same dates) for other demo
    /// people across September–October 2026, on days they were employed. Idempotent: anyone who already has an absence
    /// is skipped.
    /// </summary>
    private async Task SeedAbsencesAsync(CancellationToken cancellationToken)
    {
        var emails = People.Select(p => EmailFor(p.FullName)).ToList();
        var people = await db.People.AsNoTracking()
            .Where(p => p.Email != null && emails.Contains(p.Email))
            .OrderBy(p => p.CodeNumber)
            .Select(p => new
            {
                p.Id,
                p.FullName,
                HasAbsences = db.Absences.Any(a => a.PersonId == p.Id),
                Spans = db.EmploymentPeriods.Where(e => e.PersonId == p.Id).Select(e => new EmploymentSpan(e.StartDate, e.EndDate)).ToList(),
            })
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;
        var added = 0;
        foreach (var (fullName, absences) in GoldenAbsences)
        {
            if (people.SingleOrDefault(p => p.FullName == fullName) is { HasAbsences: false } person)
            {
                foreach (var (date, portion) in absences)
                {
                    db.Absences.Add(Absence.Create(person.Id, DateOnly.Parse(date, CultureInfo.InvariantCulture), portion, "Demo data", ActorId, now));
                    added++;
                }
            }
        }

        var first = new DateOnly(2026, 9, 1);
        foreach (var person in people.Where(p => !p.HasAbsences && GoldenAbsences.All(g => g.FullName != p.FullName)))
        {
            // One seed per demo person (their place in the list), so skipping someone never shifts anyone else's dates.
            var random = new Random(202610 + Array.FindIndex(People, p => p.FullName == person.FullName));
            var count = random.Next(0, 4); // 0–3 absences each
            var used = new HashSet<DateOnly>();
            for (var i = 0; i < count; i++)
            {
                var date = first.AddDays(random.Next(0, 61)); // Sep 1 – Oct 31
                var portion = random.Next(0, 3) == 0 ? AbsencePortion.Half : AbsencePortion.Full;
                if (WorkingDays.IsWorkingDay(date) && EmploymentCalendar.IsEmployedOn(person.Spans, date) && used.Add(date))
                {
                    db.Absences.Add(Absence.Create(person.Id, date, portion, null, ActorId, now));
                    added++;
                }
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            SecurityLog.DemoAbsencesSeeded(_log, added);
        }
    }

    /// <summary>The Manager who "created" demo increments (no such login exists; shown as "—").</summary>
    public const string DemoManagerId = "demo-manager";

    /// <summary>
    /// Demo pay mirroring SPEC §9 (Ayesha = G1 CompanyRecommended $300 + $25; Bilal = G4 BudgetHire $1,000 budget /
    /// Rs 196,000 pay; Imran = G6 Owner $1,200, all from 2026-10-01), plus a few people with a year of changes.
    /// Idempotent: people who already have pay records are skipped.
    /// </summary>
    private static readonly DemoPay[] Pay =
    [
        new("Ayesha Siddiqui", [new("2026-10-01", 300m, 25m, 300m, Domain.Pay.PayCurrency.USD, ActorId)]),
        new("Bilal Ahmed", [new("2026-10-01", 1000m, 0m, 196_000m, Domain.Pay.PayCurrency.PKR, ActorId)]),
        new("Imran Qureshi", [new("2026-10-01", 1200m, 0m, 1200m, Domain.Pay.PayCurrency.USD, ActorId)]),
        new("Kamran Yousaf", [new("2026-10-01", 300m, 25m, 300m, Domain.Pay.PayCurrency.USD, ActorId)]),
        new("Nadia Haider", [new("2026-10-01", 300m, 25m, 300m, Domain.Pay.PayCurrency.USD, ActorId)]),
        new("Rizwan Ali", [new("2026-10-01", 300m, 25m, 300m, Domain.Pay.PayCurrency.USD, ActorId)]),
        new("Fatima Zahra",
        [
            new("2025-10-01", 800m, 25m, 800m, Domain.Pay.PayCurrency.USD, ActorId),
            new("2026-04-01", 900m, 25m, 900m, Domain.Pay.PayCurrency.USD, DemoManagerId),
        ]),
        new("Hamza Sheikh",
        [
            new("2025-11-01", 1500m, 0m, 280_000m, Domain.Pay.PayCurrency.PKR, ActorId),
            new("2026-05-16", 1500m, 0m, 310_000m, Domain.Pay.PayCurrency.PKR, DemoManagerId, NeedsReview: true),
        ]),
        new("Mariam Javed",
        [
            new("2025-12-01", 1200m, 0m, 850m, Domain.Pay.PayCurrency.USD, ActorId),
            new("2026-07-01", 1200m, 0m, 900m, Domain.Pay.PayCurrency.USD, DemoManagerId, NeedsReview: true),
        ]),
        new("Omar Farooq",
        [
            new("2025-02-16", 1000m, 30m, 1000m, Domain.Pay.PayCurrency.USD, ActorId),
            new("2026-01-01", 1000m, 35m, 1000m, Domain.Pay.PayCurrency.USD, ActorId), // commission only: BillingChange
        ]),
        new("Ali Raza",
        [
            new("2025-04-01", 700m, 25m, 700m, Domain.Pay.PayCurrency.USD, ActorId),
            new("2026-04-01", 780m, 25m, 780m, Domain.Pay.PayCurrency.USD, DemoManagerId),
        ]),
    ];

    private async Task SeedRateRecordsAsync(CancellationToken cancellationToken)
    {
        var emails = Pay.Select(p => EmailFor(p.FullName)).ToList();
        var people = await db.People.AsNoTracking()
            .Where(p => p.Email != null && emails.Contains(p.Email))
            .Select(p => new { p.Id, p.Email, HasRecords = db.RateRecords.Any(r => r.PersonId == p.Id) })
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var demo in Pay)
        {
            var person = people.SingleOrDefault(p => p.Email == EmailFor(demo.FullName));
            if (person is null || person.HasRecords)
            {
                continue;
            }

            Domain.Pay.PayTerms? previous = null;
            foreach (var r in demo.Records)
            {
                var terms = new Domain.Pay.PayTerms(DateOnly.Parse(r.EffectiveFrom, CultureInfo.InvariantCulture), r.Billed, r.Commission, r.Pay, r.Currency);
                var record = Domain.Pay.RateRecord.Create(person.Id, terms, "Demo data", r.NeedsReview, r.CreatedBy, clock.UtcNow);
                record.SetChangeType(Domain.Pay.PayRules.DeriveChangeType(previous, terms, null));
                db.RateRecords.Add(record);
                previous = terms;
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed record DemoPay(string FullName, DemoRate[] Records);

    private sealed record DemoRate(string EffectiveFrom, decimal Billed, decimal Commission, decimal Pay, Domain.Pay.PayCurrency Currency, string CreatedBy, bool NeedsReview = false);

    private static string EmailFor(string fullName) =>
        fullName.ToLowerInvariant().Replace(' ', '.') + EmailDomain;

    private sealed record DemoPerson(string FullName, PersonType Type, string Designation, string Joined, string? Left, HireSource? Source);
}
