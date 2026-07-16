using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>SUG-11 §4's vault plumbing: the rejection note round-trips writer → reader,
/// follows the record kinds' append-only naming, and the reader picks the latest attempt.</summary>
public sealed class RejectionNoteTests : IDisposable
{
    private readonly string _vault =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    private readonly VaultLayout _layout;
    private readonly VaultWriter _writer;
    private readonly VaultReader _reader;

    private static readonly DateTimeOffset Stamp = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    public RejectionNoteTests()
    {
        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "recipe-tagging"));
        _layout = new VaultLayout(_vault, "Plans");
        _writer = new VaultWriter(_layout);
        _reader = new VaultReader(_layout);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_vault, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Round_trips_the_note_with_provenance_frontmatter()
    {
        var path = _writer.WriteRejectionNote("recipe-tagging", "01-tag-entity", 1, "wrong naming", Stamp);

        Assert.Equal(_layout.RejectionFile("recipe-tagging", "01-tag-entity", 1), path);
        var text = File.ReadAllText(path);
        Assert.Contains("plan_id: recipe-tagging", text);
        Assert.Contains("attempt: 1", text);
        Assert.Contains("timestamp: 2026-07-17T08:00:00Z", text);

        var rejection = _reader.ReadLatestRejection("recipe-tagging", "01-tag-entity");
        Assert.NotNull(rejection);
        Assert.Equal(1, rejection.Attempt);
        Assert.Equal("wrong naming", rejection.Note);
    }

    [Fact]
    public void Reader_returns_the_highest_attempt_and_ignores_other_checkpoints()
    {
        _writer.WriteRejectionNote("recipe-tagging", "01-tag-entity", 1, "first reason", Stamp);
        _writer.WriteRejectionNote("recipe-tagging", "01-tag-entity", 3, "third reason", Stamp);
        _writer.WriteRejectionNote("recipe-tagging", "02-endpoint", 5, "other checkpoint", Stamp);

        var rejection = _reader.ReadLatestRejection("recipe-tagging", "01-tag-entity");

        Assert.Equal(3, rejection!.Attempt);
        Assert.Equal("third reason", rejection.Note);
    }

    [Fact]
    public void No_directory_means_no_rejection_not_an_error()
    {
        Assert.Null(_reader.ReadLatestRejection("recipe-tagging", "01-tag-entity"));
    }

    [Fact]
    public void Notes_are_append_only_like_every_record_kind()
    {
        _writer.WriteRejectionNote("recipe-tagging", "01-tag-entity", 1, "first", Stamp);

        var ex = Assert.Throws<VaultException>(() =>
            _writer.WriteRejectionNote("recipe-tagging", "01-tag-entity", 1, "second", Stamp));
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void An_empty_note_is_refused_before_touching_disk()
    {
        Assert.Throws<ArgumentException>(() =>
            _writer.WriteRejectionNote("recipe-tagging", "01-tag-entity", 1, "  ", Stamp));
        Assert.False(Directory.Exists(_layout.RejectionDirectory("recipe-tagging")));
    }
}
