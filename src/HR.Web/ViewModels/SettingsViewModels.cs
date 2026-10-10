using HR.Domain.Settings;

namespace HR.Web.ViewModels;

/// <summary>The Settings form (Admin only). Validation lives in <see cref="SettingsRules"/>; only the row version is extra.</summary>
public sealed class SettingsForm
{
    public string? BusinessName { get; set; }

    public string? BusinessAddress { get; set; }

    public string? BusinessEmail { get; set; }

    public string? BusinessPhone { get; set; }

    public string? BankName { get; set; }

    public string? BankAccountTitle { get; set; }

    public string? BankAccountNumber { get; set; }

    public string? BankSwift { get; set; }

    public string? ClientName { get; set; }

    public string? ClientAddress { get; set; }

    public string? ClientContactPerson { get; set; }

    public string? ClientEmail { get; set; }

    public string? InvoicePrefix { get; set; }

    public int? PaymentTermsDays { get; set; }

    public string? InvoiceFooter { get; set; }

    public string? PayslipIssuerName { get; set; }

    public string? RowVersion { get; set; }

    public SettingsInput ToInput() => new(BusinessName, BusinessAddress, BusinessEmail, BusinessPhone, BankName, BankAccountTitle, BankAccountNumber,
        BankSwift, ClientName, ClientAddress, ClientContactPerson, ClientEmail, InvoicePrefix, PaymentTermsDays, InvoiceFooter, PayslipIssuerName);

    public static SettingsForm From(AppSettings s) => new()
    {
        BusinessName = s.BusinessName,
        BusinessAddress = s.BusinessAddress,
        BusinessEmail = s.BusinessEmail,
        BusinessPhone = s.BusinessPhone,
        BankName = s.BankName,
        BankAccountTitle = s.BankAccountTitle,
        BankAccountNumber = s.BankAccountNumber,
        BankSwift = s.BankSwift,
        ClientName = s.ClientName,
        ClientAddress = s.ClientAddress,
        ClientContactPerson = s.ClientContactPerson,
        ClientEmail = s.ClientEmail,
        InvoicePrefix = s.InvoicePrefix,
        PaymentTermsDays = s.PaymentTermsDays,
        InvoiceFooter = s.InvoiceFooter,
        PayslipIssuerName = s.PayslipIssuerName,
        RowVersion = Convert.ToBase64String(s.RowVersion),
    };
}

public sealed record SettingsPage(SettingsForm Form, bool CanIssueInvoices, DateTimeOffset? UpdatedAt, bool Conflict);

/// <summary>One text field on the settings page.</summary>
public sealed record SettingsField(string Name, string Label, string? Value, string Type = "text", string? Hint = null, bool Multiline = false, string? Autocomplete = null);
