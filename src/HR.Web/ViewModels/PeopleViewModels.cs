using System.ComponentModel.DataAnnotations;
using HR.Domain.People;
using HR.Infrastructure.People;

namespace HR.Web.ViewModels;

/// <summary>
/// The create/edit form for Managers and Admins alike. It has no hire-source field at all, so a posted
/// HireSource value can never bind to anything (no over-posting).
/// </summary>
public sealed class PersonFormViewModel
{
    [Required(ErrorMessage = "Enter the full name.")]
    [StringLength(PersonInput.FullNameMaxLength, ErrorMessage = "The full name can be at most 200 characters.")]
    [Display(Name = "Full name")]
    public string? FullName { get; set; }

    [Required(ErrorMessage = "Choose Employee or Internee.")]
    [Display(Name = "Person type")]
    public PersonType? Type { get; set; }

    [Required(ErrorMessage = "Enter the designation.")]
    [StringLength(PersonInput.DesignationMaxLength, ErrorMessage = "The designation can be at most 100 characters.")]
    [Display(Name = "Designation")]
    public string? Designation { get; set; }

    [EmailAddress(ErrorMessage = "Enter a valid email address, or leave it empty.")]
    [StringLength(PersonInput.EmailMaxLength)]
    [Display(Name = "Email (optional)")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Enter a phone number.")]
    [RegularExpression(@"^\s*\+?[0-9\s\-().]{9,20}\s*$", ErrorMessage = "Enter a Pakistani phone number, such as 0300-1234567 or +92 300 1234567.")]
    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [RegularExpression(@"^\s*\d{5}-?\d{7}-?\d\s*$", ErrorMessage = "Enter the CNIC as 12345-1234567-1 (13 digits), or leave it empty.")]
    [Display(Name = "CNIC (optional)")]
    public string? Cnic { get; set; }

    [StringLength(PersonInput.BankNameMaxLength, ErrorMessage = "The bank name can be at most 100 characters.")]
    [Display(Name = "Bank name (optional)")]
    public string? BankName { get; set; }

    [RegularExpression(@"^\s*[Pp][Kk][\s\-]*\d[\s\-]*\d[\s\-]*([A-Za-z][\s\-]*){4}([A-Za-z0-9][\s\-]*){16}$",
        ErrorMessage = "Enter a valid Pakistani IBAN: PK, 2 check digits, a 4-letter bank code and 16 letters or digits.")]
    [Display(Name = "IBAN (optional)")]
    public string? Iban { get; set; }

    [Required(ErrorMessage = "Enter the joining date.")]
    [DataType(DataType.Date)]
    [Display(Name = "Joining date")]
    public DateOnly? JoiningDate { get; set; }

    [StringLength(PersonInput.NotesMaxLength, ErrorMessage = "Notes can be at most 1000 characters.")]
    [Display(Name = "Notes (optional)")]
    public string? Notes { get; set; }

    /// <summary>Edit only: the stored CNIC is never sent to the edit form; ticking this removes it.</summary>
    public bool RemoveCnic { get; set; }

    /// <summary>Edit only: ticking this removes the stored IBAN.</summary>
    public bool RemoveIban { get; set; }

    /// <summary>Edit only: the concurrency token the form was loaded with (base64).</summary>
    public string? RowVersion { get; set; }

    public PersonInput ToInput() => new(FullName, Type, Designation, Email, Phone, Cnic, BankName, Iban, JoiningDate, Notes);

    public static PersonFormViewModel From(PersonDetails details) => new()
    {
        FullName = details.FullName,
        Type = details.Type,
        Designation = details.Designation,
        Email = details.Email,
        Phone = PakistaniPhone.Format(details.Phone),
        BankName = details.BankName,
        JoiningDate = details.JoiningDate,
        Notes = details.Notes,
        RowVersion = Convert.ToBase64String(details.RowVersion),
        // CNIC and IBAN are deliberately left empty: "leave blank to keep".
    };
}

/// <summary>One field that differs between what the user submitted and what is now saved (concurrency conflict).</summary>
public sealed record FieldChangeViewModel(string Label, string Saved, string Yours);

public sealed record PersonEditViewModel(
    int Id,
    string Code,
    bool IsActive,
    string? MaskedCnic,
    string? MaskedIban,
    PersonFormViewModel Form,
    IReadOnlyList<FieldChangeViewModel>? ConflictChanges = null);

public sealed record PersonRowViewModel(int Id, string Code, string FullName, string Designation, PersonType Type, DateOnly JoiningDate, DateOnly? LeavingDate, bool IsActive);

public sealed record PeopleListViewModel(
    IReadOnlyList<PersonRowViewModel> Rows,
    string? Search,
    PersonType? Type,
    PersonStatusFilter Status,
    string Sort,
    int Page,
    int TotalPages,
    int TotalCount,
    AdminPeopleListViewModel? Admin)
{
    public bool HasFilter => !string.IsNullOrWhiteSpace(Search) || Type is not null || Status != PersonStatusFilter.Active
        || Admin is { Filter: not HireSourceFilter.All };

    /// <summary>Route values for list links (paging), keeping the current filters. Admin-only values only for Admins.</summary>
    public Dictionary<string, string> RouteFor(int page)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            values["q"] = Search;
        }

        if (Type is { } type)
        {
            values["type"] = type.ToString();
        }

        if (Status != PersonStatusFilter.Active)
        {
            values["status"] = Status.ToString();
        }

        if (Sort != PeopleSortOptions.Default)
        {
            values["sort"] = Sort;
        }

        if (Admin is { Filter: not HireSourceFilter.All } admin)
        {
            values["hireSource"] = admin.Filter.ToString();
        }

        return values;
    }
}

/// <summary>Admin-only parts of the list. Null for Managers, so nothing about hire sources is even loaded.</summary>
public sealed record AdminPeopleListViewModel(HireSourceFilter Filter, IReadOnlyDictionary<int, HireSource?> HireSources);

public static class PeopleSortOptions
{
    public const string Default = "name";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("name", "Name (A–Z)"),
        ("name_desc", "Name (Z–A)"),
        ("code", "Code (oldest first)"),
        ("code_desc", "Code (newest first)"),
        ("joined_desc", "Joined (newest first)"),
        ("joined", "Joined (oldest first)"),
    ];

    public static (PersonSort Sort, bool Descending, string Value) Parse(string? value) => value switch
    {
        "name_desc" => (PersonSort.Name, true, "name_desc"),
        "code" => (PersonSort.Code, false, "code"),
        "code_desc" => (PersonSort.Code, true, "code_desc"),
        "joined" => (PersonSort.Joined, false, "joined"),
        "joined_desc" => (PersonSort.Joined, true, "joined_desc"),
        _ => (PersonSort.Name, false, Default),
    };
}

public sealed record PersonDetailsViewModel(
    PersonDetails Person,
    DateOnly DefaultLeavingDate,
    DateOnly? MinRejoiningDate,
    DateOnly DefaultRejoiningDate,
    HireSourceCardViewModel? AdminHireSource,
    IReadOnlyList<EmploymentPeriodRowViewModel> EmploymentHistory,
    AdminPayTabViewModel? AdminPay,
    ManagerPayTabViewModel? ManagerPay,
    PersonAbsenceTabViewModel? Absences = null)
{
    public bool ShowAbsences => Absences is not null;
}

/// <summary>One employment period on the details page. Working days run to today for the current (open) period.</summary>
public sealed record EmploymentPeriodRowViewModel(DateOnly Start, DateOnly? End, int WorkingDays);

/// <summary>Admin-only hire-source card on the details page. Null for Managers.</summary>
/// <param name="Locked">True while the person has pay records: the source can't change until they are deleted.</param>
public sealed record HireSourceCardViewModel(HireSource? Current, bool Locked);

public sealed record PeopleTileViewModel(int Active, int Employees, int Internees);
