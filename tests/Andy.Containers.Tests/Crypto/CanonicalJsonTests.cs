using System.Text;
using Andy.Containers.Crypto;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Crypto;

// IM3 (rivoli-ai/andy-containers#252). The canonical-JSON helper backs
// the content-addressable spec hash that determines build idempotency.
// If two specs that should hash to the same value drift apart, the
// cache stops working; if two distinct specs collide, builds get
// served the wrong artifact. These tests pin both directions.
public class CanonicalJsonTests
{
    [Fact]
    public void Serialize_OrdersObjectKeysLexicographically()
    {
        var canonical = AsString(CanonicalJson.Serialize("""{"b":1,"a":2}"""));
        canonical.Should().Be("""{"a":2,"b":1}""");
    }

    [Fact]
    public void Serialize_OrdersAtNestedLevels()
    {
        var canonical = AsString(CanonicalJson.Serialize("""{"outer":{"z":1,"a":2}}"""));
        canonical.Should().Be("""{"outer":{"a":2,"z":1}}""");
    }

    [Fact]
    public void Serialize_PreservesArrayOrder()
    {
        var canonical = AsString(CanonicalJson.Serialize("""[3,1,2]"""));
        canonical.Should().Be("""[3,1,2]""");
    }

    [Fact]
    public void Serialize_DropsInsignificantWhitespace()
    {
        var pretty = """
            {
              "a": 1,
              "b": [ 1, 2, 3 ],
              "c": {
                "z": "x",
                "y": "w"
              }
            }
            """;
        var compact = """{"a":1,"b":[1,2,3],"c":{"y":"w","z":"x"}}""";

        AsString(CanonicalJson.Serialize(pretty)).Should().Be(compact);
    }

    [Fact]
    public void Serialize_HandlesAllScalarKinds()
    {
        var json = """{"s":"hello","n":42,"f":3.14,"t":true,"f2":false,"x":null}""";
        var canonical = AsString(CanonicalJson.Serialize(json));
        // Keys sorted: f, f2, n, s, t, x
        canonical.Should().Be("""{"f":3.14,"f2":false,"n":42,"s":"hello","t":true,"x":null}""");
    }

    [Fact]
    public void Hash_StableAcrossWhitespaceAndKeyOrder()
    {
        var first = CanonicalJson.Hash("""{"name":"foo","version":"1.0.0","packages":["a","b"]}""");
        var second = CanonicalJson.Hash("""
              {
                "version": "1.0.0",
                "name": "foo",
                "packages":   [  "a", "b"  ]
              }
            """);

        second.Should().Be(first,
            "two equivalent specs that differ only in whitespace and key order must hash the same.");
    }

    [Fact]
    public void Hash_DiffersWhenContentChanges()
    {
        var foo = CanonicalJson.Hash("""{"name":"foo"}""");
        var bar = CanonicalJson.Hash("""{"name":"bar"}""");
        bar.Should().NotBe(foo);
    }

    [Fact]
    public void Hash_PreservesArrayOrder()
    {
        // Arrays are positional even after canonicalisation — reordering
        // ["a","b"] to ["b","a"] yields a different hash.
        var ab = CanonicalJson.Hash("""{"packages":["a","b"]}""");
        var ba = CanonicalJson.Hash("""{"packages":["b","a"]}""");
        ba.Should().NotBe(ab,
            "arrays are ordered — re-ordering them changes the spec.");
    }

    [Fact]
    public void Hash_FormatIsSha256ColonHex()
    {
        var hash = CanonicalJson.Hash("""{}""");
        hash.Should().StartWith("sha256:");
        hash.Length.Should().Be("sha256:".Length + 64,
            "SHA-256 produces 32 bytes / 64 hex characters.");
        hash.Substring(7).Should().MatchRegex("^[0-9a-f]{64}$",
            "hex output is lowercase, no separators.");
    }

    [Fact]
    public void Hash_KnownVector()
    {
        // Pin a known canonical hash so accidental changes to the
        // canonicalisation algorithm are caught — they would break
        // every cache entry written before the change.
        var hash = CanonicalJson.Hash("""{}""");
        hash.Should().Be("sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            "{} canonicalises to {} which has a fixed SHA-256.");
    }

    [Fact]
    public void Serialize_ThrowsOnInvalidJson()
    {
        var act = () => CanonicalJson.Serialize("{not-valid}");
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void ComputeSpecHash_DependsOnSpecAndFileDigests()
    {
        var canonicalSpec = CanonicalJson.Serialize("""{"name":"foo"}""");
        var withoutFiles = CanonicalJson.ComputeSpecHash(canonicalSpec, new Dictionary<string, string>());
        var withFile = CanonicalJson.ComputeSpecHash(
            canonicalSpec,
            new Dictionary<string, string> { ["script.sh"] = "sha256:abc" });

        withFile.Should().NotBe(withoutFiles,
            "adding a file to the multipart upload must change the spec hash.");
    }

    [Fact]
    public void ComputeSpecHash_IsStableUnderFileMapInsertOrder()
    {
        var canonicalSpec = CanonicalJson.Serialize("""{"name":"foo"}""");
        var ab = CanonicalJson.ComputeSpecHash(
            canonicalSpec,
            new Dictionary<string, string> { ["a.sh"] = "sha256:1", ["b.sh"] = "sha256:2" });
        var ba = CanonicalJson.ComputeSpecHash(
            canonicalSpec,
            new Dictionary<string, string> { ["b.sh"] = "sha256:2", ["a.sh"] = "sha256:1" });

        ba.Should().Be(ab,
            "file digests are sorted before hashing, so insert order is irrelevant.");
    }

    [Fact]
    public void ComputeSpecHash_DiffersWhenLogicalFileNameChanges()
    {
        var canonicalSpec = CanonicalJson.Serialize("""{"name":"foo"}""");
        var asA = CanonicalJson.ComputeSpecHash(
            canonicalSpec,
            new Dictionary<string, string> { ["a.sh"] = "sha256:abc" });
        var asB = CanonicalJson.ComputeSpecHash(
            canonicalSpec,
            new Dictionary<string, string> { ["b.sh"] = "sha256:abc" });

        asB.Should().NotBe(asA,
            "the logical name is part of the spec — a script being placed at a different path is a different spec.");
    }

    private static string AsString(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
