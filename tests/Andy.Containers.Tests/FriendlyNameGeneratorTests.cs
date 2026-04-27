using Andy.Containers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

// Conductor #871. The friendly-name generator is the user-visible
// handle for every container ("amber-pelican"). These tests pin
// three contracts:
//   1. Generated names match the {adjective}-{animal} shape.
//   2. The wordlists themselves contain no duplicates (otherwise
//      the math below would lie).
//   3. GenerateAvoiding(taken) really does avoid `taken`, even
//      when `taken` covers most of the namespace.
//
// The user explicitly asked for collision regression coverage —
// "make sure we have testing not to have name collisions."
public class FriendlyNameGeneratorTests
{
    [Fact]
    public void Generate_ReturnsAdjectiveDashAnimal()
    {
        var name = FriendlyNameGenerator.Generate(new Random(42));

        name.Should().Contain("-");
        var parts = name.Split('-');
        parts.Should().HaveCount(2);
        FriendlyNameGenerator.Adjectives.Should().Contain(parts[0]);
        FriendlyNameGenerator.Animals.Should().Contain(parts[1]);
    }

    [Fact]
    public void Adjectives_ContainsNoDuplicates()
    {
        FriendlyNameGenerator.Adjectives.Should()
            .OnlyHaveUniqueItems("a duplicate adjective inflates the perceived namespace and biases generation");
    }

    [Fact]
    public void Animals_ContainsNoDuplicates()
    {
        FriendlyNameGenerator.Animals.Should()
            .OnlyHaveUniqueItems("a duplicate animal inflates the perceived namespace and biases generation");
    }

    [Fact]
    public void Wordlist_HasAtLeastTwoThousandCombinations()
    {
        // The story-level cap a user can hit is ~32 simultaneous
        // containers (per the per-user quota). At 2K+ combinations
        // and `GenerateAvoiding`, collisions become mathematically
        // unreachable. If someone shrinks the lists, this test
        // fails so we have to revisit the suffix-fallback path.
        var combinations = FriendlyNameGenerator.Adjectives.Length
            * FriendlyNameGenerator.Animals.Length;
        combinations.Should().BeGreaterThan(2000);
    }

    [Fact]
    public void GenerateAvoiding_ProducesNoCollisions_OverThousandDraws()
    {
        // Stress test: simulate filling up the namespace one
        // container at a time. Each draw must avoid all prior
        // draws. We stop short of fully exhausting the wordlist
        // because once nearly-full the generator legitimately
        // needs the suffix fallback — that's covered separately.
        var rng = new Random(1234);
        var seen = new HashSet<string>();

        for (var i = 0; i < 1000; i++)
        {
            var name = FriendlyNameGenerator.GenerateAvoiding(seen, rng);
            seen.Add(name).Should()
                .BeTrue($"draw #{i} returned a name already in the avoid set: {name}");
        }
    }

    [Fact]
    public void GenerateAvoiding_FallsBackToSuffix_WhenNamespaceExhausted()
    {
        // Pre-populate `taken` with every possible {adj}-{animal}
        // combination. The generator can't find a fresh pick, so
        // it must append "-2".
        var taken = new HashSet<string>();
        foreach (var adj in FriendlyNameGenerator.Adjectives)
        foreach (var animal in FriendlyNameGenerator.Animals)
        {
            taken.Add($"{adj}-{animal}");
        }

        var fresh = FriendlyNameGenerator.GenerateAvoiding(taken, new Random(7));

        fresh.Should().EndWith("-2", "exhausted namespace must fall back to a numeric suffix");
        taken.Should().NotContain(fresh, "the fallback name must still be unique");
    }

    [Fact]
    public void GenerateAvoiding_IncrementsSuffix_WhenSuffixedNameAlsoTaken()
    {
        // Same exhaustion, plus the "-2" variants are also taken.
        // The generator must walk to "-3".
        var taken = new HashSet<string>();
        foreach (var adj in FriendlyNameGenerator.Adjectives)
        foreach (var animal in FriendlyNameGenerator.Animals)
        {
            taken.Add($"{adj}-{animal}");
            taken.Add($"{adj}-{animal}-2");
        }

        var fresh = FriendlyNameGenerator.GenerateAvoiding(taken, new Random(7));

        fresh.Should().EndWith("-3");
    }

    [Fact]
    public void GenerateAvoiding_ReturnsImmediately_WhenAvoidSetIsEmpty()
    {
        var name = FriendlyNameGenerator.GenerateAvoiding(new HashSet<string>(), new Random(0));

        name.Should().NotBeNullOrEmpty();
        name.Should().Contain("-");
    }
}
