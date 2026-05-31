using System.Text;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// F6.1 (rivoli-ai/conductor#1940). Pure parser: numstat + unified patch →
// per-file GitDiffFile list, with the 64 KiB-per-file truncation guard.
public class GitDiffParserTests
{
    private const string SamplePatch = """
diff --git a/src/Foo.cs b/src/Foo.cs
index 1111111..2222222 100644
--- a/src/Foo.cs
+++ b/src/Foo.cs
@@ -1,3 +1,4 @@
 line1
+added line
 line2
 line3
diff --git a/README.md b/README.md
new file mode 100644
index 0000000..3333333
--- /dev/null
+++ b/README.md
@@ -0,0 +1,2 @@
+new file
+second line
""";

    private const string SampleNumstat = """
1	0	src/Foo.cs
2	0	README.md
""";

    [Fact]
    public void Parse_NumstatAndPatch_ProducesPerFileEntries()
    {
        var files = GitDiffParser.Parse(SampleNumstat, SamplePatch);

        files.Should().HaveCount(2);
        var foo = files.Single(f => f.Path == "src/Foo.cs");
        foo.Additions.Should().Be(1);
        foo.Deletions.Should().Be(0);
        foo.ChangeType.Should().Be("modified");
        foo.Patch.Should().Contain("+added line");
        foo.Truncated.Should().BeFalse();

        var readme = files.Single(f => f.Path == "README.md");
        readme.Additions.Should().Be(2);
        readme.ChangeType.Should().Be("added");
        readme.Patch.Should().Contain("+new file");
    }

    [Fact]
    public void Parse_DeletedFile_ClassifiesAsDeleted()
    {
        const string patch = """
diff --git a/gone.txt b/gone.txt
deleted file mode 100644
index 4444444..0000000
--- a/gone.txt
+++ /dev/null
@@ -1,2 +0,0 @@
-bye
-world
""";
        const string numstat = "0\t2\tgone.txt";

        var files = GitDiffParser.Parse(numstat, patch);

        files.Should().ContainSingle();
        files[0].ChangeType.Should().Be("deleted");
        files[0].Deletions.Should().Be(2);
    }

    [Fact]
    public void Parse_BinaryFile_NumstatDashes_AdditionsDeletionsNull()
    {
        const string numstat = "-\t-\tlogo.png";
        const string patch = """
diff --git a/logo.png b/logo.png
new file mode 100644
index 0000000..5555555
Binary files /dev/null and b/logo.png differ
""";
        var files = GitDiffParser.Parse(numstat, patch);

        files.Should().ContainSingle();
        files[0].Additions.Should().BeNull();
        files[0].Deletions.Should().BeNull();
    }

    [Fact]
    public void Parse_CleanTree_EmptyInputs_ReturnsNoFiles()
    {
        var files = GitDiffParser.Parse(string.Empty, string.Empty);
        files.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MultiRepoPrefix_PrependsPathPrefix()
    {
        var files = GitDiffParser.Parse(SampleNumstat, SamplePatch, "/workspace/repoA/");
        files.Should().OnlyContain(f => f.Path.StartsWith("/workspace/repoA/"));
        files.Should().Contain(f => f.Path == "/workspace/repoA/src/Foo.cs");
    }

    [Fact]
    public void Parse_PatchOver64KiB_TruncatesAndFlags()
    {
        // Build a single-file patch whose body exceeds 64 KiB.
        var big = new StringBuilder();
        big.AppendLine("diff --git a/huge.txt b/huge.txt");
        big.AppendLine("--- a/huge.txt");
        big.AppendLine("+++ b/huge.txt");
        big.AppendLine("@@ -0,0 +1,5000 @@");
        for (var i = 0; i < 5000; i++)
            big.AppendLine("+" + new string('x', 100)); // ~500 KiB

        const string numstat = "5000\t0\thuge.txt";

        var files = GitDiffParser.Parse(numstat, big.ToString());

        files.Should().ContainSingle();
        files[0].Truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(files[0].Patch)
            .Should().BeLessThanOrEqualTo(IGitDiffService.MaxPatchBytesPerFile + 128 /* trailer */);
        files[0].Patch.Should().Contain("truncated");
    }

    [Fact]
    public void Parse_RenamedFile_UsesNewPath_AndClassifiesRenamed()
    {
        const string patch = """
diff --git a/old/name.cs b/new/name.cs
similarity index 90%
rename from old/name.cs
rename to new/name.cs
index 6666666..7777777 100644
--- a/old/name.cs
+++ b/new/name.cs
@@ -1 +1 @@
-old
+new
""";
        const string numstat = "1\t1\tnew/name.cs";

        var files = GitDiffParser.Parse(numstat, patch);

        files.Should().ContainSingle();
        files[0].ChangeType.Should().Be("renamed");
        files[0].Path.Should().Be("new/name.cs");
    }
}
