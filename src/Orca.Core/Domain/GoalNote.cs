namespace Orca.Core.Domain;

/// <summary>The Interviewer's refined goal brief (SUG-08): the "chat about the goal, get
/// a prompt" step as a durable, reviewable vault artifact. The body is what the human
/// edits in Obsidian (answering the open questions in place) and what plan-generate
/// hands the Planner as the goal brief.</summary>
public sealed record GoalNote(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset Created,
    string Body)
{
    /// <summary>The still-unanswered open questions — every Markdown task item the body
    /// carries (`- [ ] …`, by the Interviewer's contract only ever written under
    /// "## Open questions"). Plan-generate refuses while any remain: the human answers
    /// by replacing the item with prose or checking it off.</summary>
    public IReadOnlyList<string> UnansweredQuestions =>
        [.. Body.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("- [ ]", StringComparison.Ordinal))
            .Select(l => l[5..].Trim())];
}
