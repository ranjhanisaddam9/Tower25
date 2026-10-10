using HR.Domain.People;

namespace HR.Tests.Unit;

public class PakistaniPhoneTests
{
    [Theory]
    [InlineData("0300-1234567", "+923001234567")]
    [InlineData("03001234567", "+923001234567")]
    [InlineData("+92 300 1234567", "+923001234567")]
    [InlineData("+92-300-1234567", "+923001234567")]
    [InlineData("0092 300 1234567", "+923001234567")]
    [InlineData("92 300 1234567", "+923001234567")]
    [InlineData("3001234567", "+923001234567")]
    [InlineData(" (0321) 765-4321 ", "+923217654321")]
    [InlineData("042-35761234", "+924235761234")]
    [InlineData("051 2345678", "+92512345678")]
    public void Valid_numbers_are_normalised(string input, string expected)
    {
        Assert.True(PakistaniPhone.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0300-123456")]        // too short
    [InlineData("0300-12345678")]      // too long
    [InlineData("+1 415 555 0100")]    // not Pakistani
    [InlineData("+44 7700 900123")]
    [InlineData("0300-123456a")]       // letters
    [InlineData("0300_1234567")]       // odd separator
    [InlineData("0123456789")]         // national number starting with 1
    [InlineData("+92 0300 1234567")]   // trunk 0 after the country code
    public void Invalid_numbers_are_rejected(string input)
    {
        Assert.False(PakistaniPhone.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("+923001234567", "+92 300 1234567")]
    [InlineData("+924235761234", "+92 42 35761234")]
    public void Format_groups_for_display(string stored, string expected) => Assert.Equal(expected, PakistaniPhone.Format(stored));
}

public class CnicTests
{
    [Theory]
    [InlineData("12345-1234567-1", "12345-1234567-1")]
    [InlineData("1234512345671", "12345-1234567-1")]
    [InlineData(" 35202-1234567-9 ", "35202-1234567-9")]
    public void Valid_cnics_are_stored_with_dashes(string input, string expected)
    {
        Assert.True(Cnic.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("12345-123456-1")]     // 12 digits
    [InlineData("12345-12345678-1")]   // 14 digits
    [InlineData("1234-51234567-1")]    // dashes in the wrong place
    [InlineData("12345 1234567 1")]    // spaces instead of dashes
    [InlineData("12345-1234567-A")]
    [InlineData("")]
    public void Invalid_cnics_are_rejected(string input)
    {
        Assert.False(Cnic.TryNormalize(input, out _));
    }
}

public class PakistaniIbanTests
{
    private const string Valid = "PK36SCBL0000001123456702";

    [Theory]
    [InlineData(Valid)]
    [InlineData("PK36 SCBL 0000 0011 2345 6702")]
    [InlineData("pk36scbl0000001123456702")]
    public void A_valid_PK_iban_is_accepted_and_normalised(string input)
    {
        Assert.True(PakistaniIban.TryNormalize(input, out var normalized));
        Assert.Equal(Valid, normalized);
    }

    [Theory]
    [InlineData("PK37SCBL0000001123456702")]       // wrong check digit
    [InlineData("PK36SCBL0000001123456703")]       // wrong account digit
    [InlineData("PK36SCBL000000112345670")]        // 23 characters
    [InlineData("PK36SCBL00000011234567021")]      // 25 characters
    [InlineData("GB82WEST12345698765432")]         // a valid IBAN, but not Pakistani
    [InlineData("PK36SCB10000001123456702")]       // bank code must be letters
    [InlineData("PK36SCBL00000011234567_2")]
    public void Invalid_ibans_are_rejected(string input)
    {
        Assert.False(PakistaniIban.TryNormalize(input, out _));
    }

    [Fact]
    public void Format_groups_in_fours() =>
        Assert.Equal("PK36 SCBL 0000 0011 2345 6702", PakistaniIban.Format(Valid));

    [Fact]
    public void Create_builds_a_valid_iban_for_fake_demo_data()
    {
        var iban = PakistaniIban.Create("TEST", "0000000000000007");

        Assert.Equal(24, iban.Length);
        Assert.True(PakistaniIban.TryNormalize(iban, out _));
    }
}

public class MaskingTests
{
    [Theory]
    [InlineData("12345-1234567-1", "•••••-••••567-1")]
    [InlineData("PK36SCBL0000001123456702", "••••••••••••••••••••6702")]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Only_the_last_four_letters_or_digits_stay_visible(string? input, string expected) =>
        Assert.Equal(expected, Masking.MaskAllButLast(input));

    [Fact]
    public void Iban_is_masked_and_grouped() =>
        Assert.Equal("•••• •••• •••• •••• •••• 6702", Masking.Iban("PK36SCBL0000001123456702"));

    [Fact]
    public void Cnic_helper_keeps_the_dashes() =>
        Assert.Equal("•••••-••••567-1", Masking.Cnic("12345-1234567-1"));
}

public class PersonInvariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    /// <summary>"Today" for the active-status rule (active until the leaving date has passed).</summary>
    private static readonly DateOnly StatusToday = new(2026, 10, 9);

    private static PersonInput Input(DateOnly? joining = null) => new(
        " Ayesha Siddiqui ", PersonType.Employee, "Engineer", "Ayesha@Example.COM", "0300-1234567",
        "1234512345671", "Bank", "pk36 scbl 0000 0011 2345 6702", joining ?? new DateOnly(2026, 1, 5), null);

    [Fact]
    public void Create_normalises_and_starts_active_without_a_leaving_date()
    {
        var person = Person.Create(7, Input(), "actor", Now);

        Assert.Equal("HR-0007", person.Code);
        Assert.Equal("Ayesha Siddiqui", person.FullName);
        Assert.Equal("ayesha@example.com", person.Email);
        Assert.Equal("+923001234567", person.Phone);
        Assert.Equal("12345-1234567-1", person.Cnic);
        Assert.Equal("PK36SCBL0000001123456702", person.Iban);
        Assert.True(person.IsActiveOn(StatusToday));
        Assert.Null(person.LeavingDate);
        Assert.Null(person.HireSource);
    }

    [Fact]
    public void Leaving_before_joining_is_rejected()
    {
        var person = Person.Create(1, Input(new DateOnly(2026, 3, 2)), "actor", Now);

        var ex = Assert.Throws<PersonRuleException>(() => person.Deactivate(new DateOnly(2026, 3, 1), "actor", Now));
        Assert.Equal("LeavingDate", ex.Field);
        Assert.True(person.IsActiveOn(StatusToday));
        Assert.Null(person.LeavingDate);
    }

    [Fact]
    public void Deactivating_always_sets_a_leaving_date_and_reactivating_clears_it()
    {
        var person = Person.Create(1, Input(new DateOnly(2026, 3, 2)), "actor", Now);

        person.Deactivate(new DateOnly(2026, 3, 2), "actor", Now); // same day as joining is allowed
        Assert.False(person.IsActiveOn(StatusToday));
        Assert.Equal(new DateOnly(2026, 3, 2), person.LeavingDate);

        var (previousJoining, previousLeaving) = person.Reactivate(new DateOnly(2026, 5, 4), "actor", Now);
        Assert.True(person.IsActiveOn(StatusToday));
        Assert.Null(person.LeavingDate);
        Assert.Equal(new DateOnly(2026, 5, 4), person.JoiningDate);
        Assert.Equal(new DateOnly(2026, 3, 2), previousJoining);
        Assert.Equal(new DateOnly(2026, 3, 2), previousLeaving);
    }

    [Fact]
    public void Reactivation_must_be_after_the_previous_leaving_date()
    {
        var person = Person.Create(1, Input(new DateOnly(2026, 3, 2)), "actor", Now);
        person.Deactivate(new DateOnly(2026, 4, 30), "actor", Now);

        Assert.Throws<PersonRuleException>(() => person.Reactivate(new DateOnly(2026, 4, 30), "actor", Now));
        Assert.False(person.IsActiveOn(StatusToday));
        Assert.NotNull(person.LeavingDate); // still inactive with its leaving date: never inactive without one
    }

    [Fact]
    public void An_active_person_cannot_be_reactivated_and_an_inactive_one_cannot_be_deactivated()
    {
        var person = Person.Create(1, Input(), "actor", Now);
        Assert.Throws<PersonRuleException>(() => person.Reactivate(new DateOnly(2026, 6, 1), "actor", Now));

        person.Deactivate(new DateOnly(2026, 6, 1), "actor", Now);
        Assert.Throws<PersonRuleException>(() => person.Deactivate(new DateOnly(2026, 6, 2), "actor", Now));
    }

    [Fact]
    public void Joining_date_cannot_move_after_the_leaving_date()
    {
        var person = Person.Create(1, Input(new DateOnly(2026, 3, 2)), "actor", Now);
        person.Deactivate(new DateOnly(2026, 4, 30), "actor", Now);

        Assert.Throws<PersonRuleException>(() => person.UpdateDetails(Input(new DateOnly(2026, 5, 1)), "actor", Now));
    }

    [Fact]
    public void Invalid_input_lists_every_field_error()
    {
        var input = new PersonInput("", null, "", "not-an-email", "12", "123", null, "PK00", null, new string('x', 1001));

        Assert.Null(input.Normalize(out var errors));
        Assert.Equal(
            ["FullName", "Type", "Designation", "Email", "Phone", "Cnic", "Iban", "JoiningDate", "Notes"],
            errors.Select(e => e.Field).ToArray());
        Assert.Throws<PersonRuleException>(() => Person.Create(1, input, "actor", Now));
    }
}
