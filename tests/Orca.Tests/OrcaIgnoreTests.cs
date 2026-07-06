using Orca.Core.Git;

namespace Orca.Tests;

public sealed class OrcaIgnoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    public OrcaIgnoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string GitignorePath => Path.Combine(_root, ".gitignore");

    [Fact]
    public void Creates_the_gitignore_when_missing()
    {
        Assert.True(OrcaIgnore.EnsureIgnored(_root));

        Assert.Contains(".orca/", File.ReadAllLines(GitignorePath));
    }

    [Fact]
    public void Appends_without_disturbing_existing_entries()
    {
        File.WriteAllText(GitignorePath, "bin/\nobj/\n");

        Assert.True(OrcaIgnore.EnsureIgnored(_root));

        var lines = File.ReadAllLines(GitignorePath);
        Assert.Contains("bin/", lines);
        Assert.Contains("obj/", lines);
        Assert.Contains(".orca/", lines);
    }

    [Theory]
    [InlineData(".orca/")]
    [InlineData(".orca")]
    [InlineData("/.orca/")]
    [InlineData("  .orca/  ")]
    public void Recognizes_existing_spellings_and_changes_nothing(string existing)
    {
        File.WriteAllText(GitignorePath, $"bin/\n{existing}\n");
        var before = File.ReadAllText(GitignorePath);

        Assert.False(OrcaIgnore.EnsureIgnored(_root));
        Assert.Equal(before, File.ReadAllText(GitignorePath));
    }
}
