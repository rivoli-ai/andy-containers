namespace Andy.Containers;

/// <summary>
/// Generates short human-friendly identifiers for containers in the
/// form <c>{adjective}-{animal}</c> (e.g. "amber-pelican").
///
/// Conductor #871. The friendly name is a complement to the 12-char
/// short ExternalId — easy for the user to refer to in chat / docs
/// / conversation without saying "the container with the long ID."
/// Collisions are acceptable: the short ID disambiguates if two
/// containers happen to share a friendly name.
///
/// Wordlist sized at ~64 × ~64 = ~4 K combinations. Enough that a
/// typical user (≤ 50 simultaneous containers) won't hit a collision
/// in practice; if they do, the IDs still distinguish them.
/// </summary>
public static class FriendlyNameGenerator
{
    /// <summary>
    /// Returns a fresh friendly name. Uses <see cref="Random.Shared"/>
    /// by default — pass a deterministic <see cref="Random"/> for
    /// tests that need reproducible output.
    /// </summary>
    public static string Generate(Random? rng = null)
    {
        rng ??= Random.Shared;
        var adjective = Adjectives[rng.Next(Adjectives.Length)];
        var animal = Animals[rng.Next(Animals.Length)];
        return $"{adjective}-{animal}";
    }

    /// <summary>
    /// Returns a friendly name that does NOT appear in
    /// <paramref name="taken"/>. Tries up to <paramref name="maxAttempts"/>
    /// distinct random combinations; if all collide, appends a
    /// numeric suffix to disambiguate (e.g. "amber-pelican-2").
    ///
    /// The wordlist gives ~4 K combinations. Birthday-bound math:
    /// at 50 containers the collision probability is ~30%, so
    /// callers MUST supply the existing set of names if they want
    /// distinct output across the fleet. Conductor #871.
    /// </summary>
    public static string GenerateAvoiding(
        ISet<string> taken,
        Random? rng = null,
        int maxAttempts = 32)
    {
        rng ??= Random.Shared;
        for (var i = 0; i < maxAttempts; i++)
        {
            var candidate = Generate(rng);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        // Fallback: pile-up territory. 32 random picks all collided
        // — unlikely (would require >50% of the 4K space taken),
        // but still possible on a heavily-populated host. Append a
        // counter to a fresh pick.
        var basePicked = Generate(rng);
        var suffix = 2;
        while (taken.Contains($"{basePicked}-{suffix}"))
        {
            suffix++;
        }
        return $"{basePicked}-{suffix}";
    }

    /// <summary>
    /// 64 short, neutral, color/texture/mood adjectives. Curated to
    /// avoid pejoratives, ambiguous slang, or anything that might
    /// embarrass a user reading the name aloud in a meeting.
    /// </summary>
    public static readonly string[] Adjectives =
    [
        "amber", "azure", "bold", "brave", "bright", "calm", "clever", "cobalt",
        "cosmic", "crimson", "crystal", "cyber", "dapper", "dark", "deep", "drifting",
        "dusky", "eager", "echoing", "electric", "emerald", "epic", "fair", "feisty",
        "fierce", "frosty", "gentle", "ghost", "golden", "graceful", "grand", "happy",
        "hazel", "honest", "indigo", "jade", "jolly", "lively", "loyal", "lucid",
        "lunar", "marble", "mellow", "merry", "mighty", "mint", "modest", "noble",
        "obsidian", "olive", "orbit", "patient", "peaceful", "pearl", "polar", "quick",
        "radiant", "rapid", "ruby", "rustic", "sage", "scarlet", "silent", "silver",
    ];

    /// <summary>
    /// 64 short animal names. Bias toward non-mammalian / quirky
    /// picks so the same handful of names doesn't dominate
    /// generated output. No domesticated species (no "cat" / "dog")
    /// to keep the names visually distinctive.
    /// </summary>
    public static readonly string[] Animals =
    [
        "albatross", "antelope", "badger", "bison", "caribou", "chimera", "cobra", "condor",
        "coral", "crane", "crocus", "dolphin", "dragon", "echidna", "elk", "falcon",
        "ferret", "finch", "fox", "gazelle", "gecko", "griffin", "hare", "hawk",
        "heron", "hyena", "ibex", "iguana", "jaguar", "jay", "kestrel", "koala",
        "kraken", "leopard", "lion", "lynx", "manta", "marlin", "narwhal", "newt",
        "ocelot", "octopus", "orca", "osprey", "otter", "owl", "panda", "panther",
        "pelican", "phoenix", "puffin", "puma", "quokka", "raven", "salamander", "seal",
        "shark", "stoat", "swan", "tiger", "viper", "vulture", "walrus", "wolverine",
    ];
}
