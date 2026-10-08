using HR.Infrastructure.Identity;

namespace HR.Tests.Unit;

public class TemporaryPasswordGeneratorTests
{
    private readonly TemporaryPasswordGenerator _generator = new();

    [Fact]
    public void Passwords_are_14_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(14, _generator.Generate().Length);
        }
    }

    [Fact]
    public void Passwords_always_meet_the_policy()
    {
        for (var i = 0; i < 1000; i++)
        {
            var password = _generator.Generate();
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.True(password.Length >= 10);
            Assert.All(password, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"Unexpected character '{c}'"));
        }
    }

    [Fact]
    public void Passwords_skip_look_alike_characters()
    {
        var all = string.Concat(Enumerable.Range(0, 500).Select(_ => _generator.Generate()));

        Assert.DoesNotContain('0', all);
        Assert.DoesNotContain('O', all);
        Assert.DoesNotContain('1', all);
        Assert.DoesNotContain('l', all);
        Assert.DoesNotContain('I', all);
    }

    [Fact]
    public void Thousand_samples_have_no_repeats()
    {
        var samples = Enumerable.Range(0, 1000).Select(_ => _generator.Generate()).ToList();

        Assert.Equal(samples.Count, samples.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Guaranteed_characters_are_not_always_in_the_same_positions()
    {
        var firstIsUpper = Enumerable.Range(0, 200).Count(_ => char.IsUpper(_generator.Generate()[0]));

        Assert.InRange(firstIsUpper, 1, 199);
    }
}
