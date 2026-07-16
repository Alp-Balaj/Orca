using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

public class ReconcilerPromptTests
{
    [Fact]
    public void Assemble_normalizes_a_crlf_diff_into_the_fenced_block_without_mangling_it()
    {
        // The Windows-machine-Claude-output path: a diff arriving with CRLF line endings
        // must come through the fence readable and fully LF-normalized, not truncated or
        // left with stray \r characters (SUG-15 — this seam had zero coverage before).
        var identity = new AttemptIdentity("recipe-tagging", 1, "01-tag-entity", 1);
        var crlfDiff = "+added line one\r\n-removed line two\r\n context line three\r\n";

        var prompt = ReconcilerPrompt.Assemble(identity, ".staging/x.md", "Plan section.", crlfDiff);

        Assert.Contains(
            "````diff\n+added line one\n-removed line two\n context line three\n````\n", prompt);
        Assert.DoesNotContain("\r", prompt);
    }
}
