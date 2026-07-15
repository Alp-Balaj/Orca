namespace Orca.Core.Run;

/// <summary>The "first free number" scan shared by every append-only numbering scheme in
/// Orca (role invocations, planning attempts, checkpoint attempts): starting at
/// <paramref name="minimum"/>, walk forward while <paramref name="exists"/> says the
/// number is already taken. Evidence is append-only, so a gap is never reused and a
/// crashed attempt's leftover file is skipped past, never overwritten.</summary>
public static class NextFreeSequence
{
    public static int Find(int minimum, Func<int, bool> exists)
    {
        var candidate = minimum;
        while (exists(candidate))
            candidate++;
        return candidate;
    }
}
