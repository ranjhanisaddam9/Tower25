using System.Text.RegularExpressions;
using HR.Domain.People;

namespace HR.Domain.Settings;

/// <summary>What the Settings form submits. Blank strings mean "not set".</summary>
public sealed record SettingsInput(
    string? BusinessName,
    string? BusinessAddress,
    string? BusinessEmail,
    string? BusinessPhone,
    string? BankName,
    string? BankAccountTitle,
    string? BankAccountNumber,
    string? BankSwift,
    string? ClientName,
    string? ClientAddress,
    string? ClientContactPerson,
    string? ClientEmail,
    string? InvoicePrefix,
    int? PaymentTermsDays,
    string? InvoiceFooter,
    string? PayslipIssuerName);

public sealed record SettingsError(string Field, string Message);

/// <summary>Limits and validation for <see cref="AppSettings"/> (M8 Part B).</summary>
public static partial class SettingsRules
{
    public const string DefaultInvoicePrefix = "INV";
    public const int DefaultPaymentTermsDays = 7;
    public const string DefaultPayslipIssuerName = "HR Payroll";
    public const int MaxPaymentTermsDays = 120;

    public static readonly IReadOnlyDictionary<string, int> MaxLengths = new Dictionary<string, int>
    {
        [nameof(SettingsInput.BusinessName)] = 200,
        [nameof(SettingsInput.BusinessAddress)] = 500,
        [nameof(SettingsInput.BusinessEmail)] = 254,
        [nameof(SettingsInput.BusinessPhone)] = 40,
        [nameof(SettingsInput.BankName)] = 100,
        [nameof(SettingsInput.BankAccountTitle)] = 150,
        [nameof(SettingsInput.BankAccountNumber)] = 40,
        [nameof(SettingsInput.BankSwift)] = 11,
        [nameof(SettingsInput.ClientName)] = 200,
        [nameof(SettingsInput.ClientAddress)] = 500,
        [nameof(SettingsInput.ClientContactPerson)] = 150,
        [nameof(SettingsInput.ClientEmail)] = 254,
        [nameof(SettingsInput.InvoicePrefix)] = 10,
        [nameof(SettingsInput.InvoiceFooter)] = 500,
        [nameof(SettingsInput.PayslipIssuerName)] = 100,
    };

    /// <summary>Trims every value and checks it. Returns the normalized input, or null with the errors.</summary>
    public static SettingsInput? Normalize(SettingsInput input, out List<SettingsError> errors)
    {
        errors = [];
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);

        var n = input with
        {
            BusinessName = T(input.BusinessName),
            BusinessAddress = T(input.BusinessAddress),
            BusinessEmail = T(input.BusinessEmail)?.ToLowerInvariant(),
            BusinessPhone = T(input.BusinessPhone),
            BankName = T(input.BankName),
            BankAccountTitle = T(input.BankAccountTitle),
            BankAccountNumber = T(input.BankAccountNumber),
            BankSwift = T(input.BankSwift)?.ToUpperInvariant(),
            ClientName = T(input.ClientName),
            ClientAddress = T(input.ClientAddress),
            ClientContactPerson = T(input.ClientContactPerson),
            ClientEmail = T(input.ClientEmail)?.ToLowerInvariant(),
            InvoicePrefix = T(input.InvoicePrefix)?.ToUpperInvariant() ?? DefaultInvoicePrefix,
            PaymentTermsDays = input.PaymentTermsDays ?? DefaultPaymentTermsDays,
            InvoiceFooter = T(input.InvoiceFooter),
            PayslipIssuerName = T(input.PayslipIssuerName),
        };

        var values = new Dictionary<string, string?>
        {
            [nameof(n.BusinessName)] = n.BusinessName, [nameof(n.BusinessAddress)] = n.BusinessAddress, [nameof(n.BusinessEmail)] = n.BusinessEmail,
            [nameof(n.BusinessPhone)] = n.BusinessPhone, [nameof(n.BankName)] = n.BankName, [nameof(n.BankAccountTitle)] = n.BankAccountTitle,
            [nameof(n.BankAccountNumber)] = n.BankAccountNumber, [nameof(n.BankSwift)] = n.BankSwift, [nameof(n.ClientName)] = n.ClientName,
            [nameof(n.ClientAddress)] = n.ClientAddress, [nameof(n.ClientContactPerson)] = n.ClientContactPerson, [nameof(n.ClientEmail)] = n.ClientEmail,
            [nameof(n.InvoicePrefix)] = n.InvoicePrefix, [nameof(n.InvoiceFooter)] = n.InvoiceFooter, [nameof(n.PayslipIssuerName)] = n.PayslipIssuerName,
        };
        foreach (var (field, value) in values)
        {
            if (value is { } v && v.Length > MaxLengths[field])
            {
                errors.Add(new SettingsError(field, $"At most {MaxLengths[field]} characters."));
            }
        }

        foreach (var field in new[] { nameof(n.BusinessEmail), nameof(n.ClientEmail) })
        {
            if (values[field] is { } email && !EmailRegex().IsMatch(email))
            {
                errors.Add(new SettingsError(field, "Enter a valid email address."));
            }
        }

        if (n.BankAccountNumber is { } account && account.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith("PK", StringComparison.OrdinalIgnoreCase))
        {
            if (PakistaniIban.TryNormalize(account, out var iban))
            {
                n = n with { BankAccountNumber = iban };
            }
            else
            {
                errors.Add(new SettingsError(nameof(n.BankAccountNumber), "This Pakistani IBAN isn't valid (PK, 2 check digits, 4-letter bank code, 16 digits)."));
            }
        }

        if (n.BankSwift is { } swift && !SwiftRegex().IsMatch(swift))
        {
            errors.Add(new SettingsError(nameof(n.BankSwift), "A SWIFT/BIC code has 8 or 11 letters and digits."));
        }

        if (!PrefixRegex().IsMatch(n.InvoicePrefix!))
        {
            errors.Add(new SettingsError(nameof(n.InvoicePrefix), "Use 1 to 10 capital letters or digits."));
        }

        if (n.PaymentTermsDays is < 0 or > MaxPaymentTermsDays)
        {
            errors.Add(new SettingsError(nameof(n.PaymentTermsDays), "Payment terms must be 0 to 120 days."));
        }

        if (n.PayslipIssuerName is null)
        {
            errors.Add(new SettingsError(nameof(n.PayslipIssuerName), "Enter the name printed on payslips."));
        }

        return errors.Count == 0 ? n : null;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex("^[A-Z0-9]{8}([A-Z0-9]{3})?$")]
    private static partial Regex SwiftRegex();

    [GeneratedRegex("^[A-Z0-9]{1,10}$")]
    private static partial Regex PrefixRegex();
}

/// <summary>
/// The single settings row (Id = 1): the business issuing invoices, the client company, invoice defaults and the payslip
/// issuer. Invoices copy the business and client details when they are issued, so later edits never change them.
/// </summary>
public sealed class AppSettings
{
    public const int SingletonId = 1;

    private AppSettings()
    {
    }

    public int Id { get; private set; } = SingletonId;

    public string? BusinessName { get; private set; }

    public string? BusinessAddress { get; private set; }

    public string? BusinessEmail { get; private set; }

    public string? BusinessPhone { get; private set; }

    public string? BankName { get; private set; }

    public string? BankAccountTitle { get; private set; }

    public string? BankAccountNumber { get; private set; }

    public string? BankSwift { get; private set; }

    public string? ClientName { get; private set; }

    public string? ClientAddress { get; private set; }

    public string? ClientContactPerson { get; private set; }

    public string? ClientEmail { get; private set; }

    public string InvoicePrefix { get; private set; } = SettingsRules.DefaultInvoicePrefix;

    public int PaymentTermsDays { get; private set; } = SettingsRules.DefaultPaymentTermsDays;

    public string? InvoiceFooter { get; private set; }

    public string PayslipIssuerName { get; private set; } = SettingsRules.DefaultPayslipIssuerName;

    public DateTimeOffset? UpdatedAt { get; private set; }

    public string? UpdatedByUserId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Invoices need both names (M8).</summary>
    public bool CanIssueInvoices => !string.IsNullOrWhiteSpace(BusinessName) && !string.IsNullOrWhiteSpace(ClientName);

    public static AppSettings CreateDefault() => new();

    public SettingsInput ToInput() => new(BusinessName, BusinessAddress, BusinessEmail, BusinessPhone, BankName, BankAccountTitle, BankAccountNumber,
        BankSwift, ClientName, ClientAddress, ClientContactPerson, ClientEmail, InvoicePrefix, PaymentTermsDays, InvoiceFooter, PayslipIssuerName);

    /// <summary>Applies normalized input; returns the names of the fields that changed (for the audit log, never values).</summary>
    public IReadOnlyList<string> Apply(SettingsInput n, string actorId, DateTimeOffset now)
    {
        var before = ToInput();
        BusinessName = n.BusinessName;
        BusinessAddress = n.BusinessAddress;
        BusinessEmail = n.BusinessEmail;
        BusinessPhone = n.BusinessPhone;
        BankName = n.BankName;
        BankAccountTitle = n.BankAccountTitle;
        BankAccountNumber = n.BankAccountNumber;
        BankSwift = n.BankSwift;
        ClientName = n.ClientName;
        ClientAddress = n.ClientAddress;
        ClientContactPerson = n.ClientContactPerson;
        ClientEmail = n.ClientEmail;
        InvoicePrefix = n.InvoicePrefix ?? SettingsRules.DefaultInvoicePrefix;
        PaymentTermsDays = n.PaymentTermsDays ?? SettingsRules.DefaultPaymentTermsDays;
        InvoiceFooter = n.InvoiceFooter;
        PayslipIssuerName = n.PayslipIssuerName ?? SettingsRules.DefaultPayslipIssuerName;
        UpdatedAt = now;
        UpdatedByUserId = actorId;

        var after = ToInput();
        return typeof(SettingsInput).GetProperties()
            .Where(p => !Equals(p.GetValue(before), p.GetValue(after)))
            .Select(p => p.Name)
            .ToList();
    }
}
