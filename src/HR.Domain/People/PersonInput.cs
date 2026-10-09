namespace HR.Domain.People;

/// <summary>A field-level rule violation. <see cref="Field"/> matches the form property name.</summary>
public sealed record PersonError(string Field, string Message);

/// <summary>Thrown when code bypasses validation and asks a <see cref="Person"/> to break an invariant.</summary>
public sealed class PersonRuleException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// The editable details of a person, as entered. <see cref="Normalize"/> validates every field and returns the
/// stored form (trimmed text, lower-case email, +92 phone, dashed CNIC, compact upper-case IBAN).
/// </summary>
public sealed record PersonInput(
    string? FullName,
    PersonType? Type,
    string? Designation,
    string? Email,
    string? Phone,
    string? Cnic,
    string? BankName,
    string? Iban,
    DateOnly? JoiningDate,
    string? Notes)
{
    public const int FullNameMaxLength = 200;
    public const int DesignationMaxLength = 100;
    public const int EmailMaxLength = 256;
    public const int BankNameMaxLength = 100;
    public const int NotesMaxLength = 1000;

    public PersonInput? Normalize(out IReadOnlyList<PersonError> errors)
    {
        var list = new List<PersonError>();

        var fullName = Text(FullName);
        if (fullName is null)
        {
            list.Add(new(nameof(FullName), "Enter the full name."));
        }
        else if (fullName.Length > FullNameMaxLength)
        {
            list.Add(new(nameof(FullName), "The full name can be at most 200 characters."));
        }

        if (Type is null || !Enum.IsDefined(Type.Value))
        {
            list.Add(new(nameof(Type), "Choose Employee or Internee."));
        }

        var designation = Text(Designation);
        if (designation is null)
        {
            list.Add(new(nameof(Designation), "Enter the designation."));
        }
        else if (designation.Length > DesignationMaxLength)
        {
            list.Add(new(nameof(Designation), "The designation can be at most 100 characters."));
        }

        var email = Text(Email)?.ToLowerInvariant();
        if (email is not null && (email.Length > EmailMaxLength || !LooksLikeEmail(email)))
        {
            list.Add(new(nameof(Email), "Enter a valid email address, or leave it empty."));
        }

        string? phone = null;
        if (Text(Phone) is null)
        {
            list.Add(new(nameof(Phone), "Enter a phone number."));
        }
        else if (!PakistaniPhone.TryNormalize(Phone, out var normalizedPhone))
        {
            list.Add(new(nameof(Phone), "Enter a Pakistani phone number, such as 0300-1234567 or +92 300 1234567."));
        }
        else
        {
            phone = normalizedPhone;
        }

        string? cnic = null;
        if (Text(Cnic) is not null)
        {
            if (People.Cnic.TryNormalize(Cnic, out var normalizedCnic))
            {
                cnic = normalizedCnic;
            }
            else
            {
                list.Add(new(nameof(Cnic), "Enter the CNIC as 12345-1234567-1 (13 digits), or leave it empty."));
            }
        }

        var bankName = Text(BankName);
        if (bankName is { Length: > BankNameMaxLength })
        {
            list.Add(new(nameof(BankName), "The bank name can be at most 100 characters."));
        }

        string? iban = null;
        if (Text(Iban) is not null)
        {
            if (PakistaniIban.TryNormalize(Iban, out var normalizedIban))
            {
                iban = normalizedIban;
            }
            else
            {
                list.Add(new(nameof(Iban), "Enter a valid Pakistani IBAN: PK, 2 check digits, a 4-letter bank code and 16 letters or digits."));
            }
        }

        if (JoiningDate is null)
        {
            list.Add(new(nameof(JoiningDate), "Enter the joining date."));
        }

        var notes = Text(Notes);
        if (notes is { Length: > NotesMaxLength })
        {
            list.Add(new(nameof(Notes), "Notes can be at most 1000 characters."));
        }

        errors = list;
        return list.Count > 0
            ? null
            : new PersonInput(fullName, Type, designation, email, phone, cnic, bankName, iban, JoiningDate, notes);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at == email.LastIndexOf('@')
            && at < email.Length - 3
            && email.IndexOf('.', at) > at + 1
            && !email.EndsWith('.')
            && !email.Any(char.IsWhiteSpace);
    }
}
