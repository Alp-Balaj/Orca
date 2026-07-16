using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>SUG-12 §1: the judge-facing diff filter — exclusion markers that keep the
/// change visible, per-file caps, and the never-filtered header lines.</summary>
public sealed class JudgeDiffTests
{
    private const string TrackedDiff =
        "diff --git a/src/App.cs b/src/App.cs\n" +
        "index 111..222 100644\n" +
        "--- a/src/App.cs\n" +
        "+++ b/src/App.cs\n" +
        "@@ -1,2 +1,3 @@\n" +
        "+var x = 1;\n" +
        "diff --git a/package-lock.json b/package-lock.json\n" +
        "index 333..444 100644\n" +
        "--- a/package-lock.json\n" +
        "+++ b/package-lock.json\n" +
        "@@ -1,2000 +1,4000 @@\n" +
        "+  \"lodash\": \"4.17.21\",\n" +
        "+  \"leftpad\": \"1.0.0\",\n";

    [Fact]
    public void Excluded_files_keep_their_header_but_lose_their_content()
    {
        var filtered = JudgeDiff.Filter(TrackedDiff, JudgeDiff.DefaultExcludes);

        Assert.Contains("diff --git a/package-lock.json b/package-lock.json", filtered);
        Assert.Contains("excluded from the judge diff by pattern 'package-lock.json'", filtered);
        Assert.Contains("the file DID change", filtered);
        Assert.DoesNotContain("lodash", filtered);
        // The real change is untouched.
        Assert.Contains("+var x = 1;", filtered);
    }

    [Fact]
    public void Wildcard_patterns_match_basenames_anywhere_in_the_tree()
    {
        var diff =
            "diff --git a/web/dist/app.min.js b/web/dist/app.min.js\n" +
            "+!function(e){var minified}\n";

        var filtered = JudgeDiff.Filter(diff, JudgeDiff.DefaultExcludes);

        Assert.Contains("pattern '*.min.js'", filtered);
        Assert.DoesNotContain("minified", filtered);
    }

    [Fact]
    public void Untracked_sections_are_filtered_by_the_same_patterns()
    {
        var diff =
            "## New file (untracked): sub/yarn.lock\n\n" +
            "lockfile contents here\n";

        var filtered = JudgeDiff.Filter(diff, JudgeDiff.DefaultExcludes);

        Assert.Contains("## New file (untracked): sub/yarn.lock", filtered);
        Assert.DoesNotContain("lockfile contents", filtered);
    }

    [Fact]
    public void An_explicit_empty_pattern_list_disables_exclusion()
    {
        var filtered = JudgeDiff.Filter(TrackedDiff, []);

        Assert.Contains("lodash", filtered);
        Assert.DoesNotContain("excluded from the judge diff", filtered);
    }

    [Fact]
    public void A_single_huge_section_is_capped_with_a_marker()
    {
        var huge = "diff --git a/src/Big.cs b/src/Big.cs\n"
            + string.Concat(Enumerable.Repeat("+x\n", 40_000));

        var filtered = JudgeDiff.Filter(huge, JudgeDiff.DefaultExcludes);

        Assert.Contains("truncated for the judges at", filtered);
        Assert.True(filtered.Length < huge.Length);
    }

    [Fact]
    public void A_diff_with_no_matches_passes_through_unchanged()
    {
        var diff =
            "diff --git a/src/App.cs b/src/App.cs\n" +
            "+var x = 1;\n";

        Assert.Equal(diff, JudgeDiff.Filter(diff, JudgeDiff.DefaultExcludes));
    }
}
