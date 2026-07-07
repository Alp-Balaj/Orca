namespace Orca.Core.Run;

/// <summary>Reads a versioned role prompt embedded at build from <c>/prompts/</c> (spec:
/// prompts as files; the packed tool must carry them). The hash recorded per run is of
/// this embedded content.</summary>
internal static class RolePromptResource
{
    internal static string Read(string resourceName)
    {
        using var stream = typeof(RolePromptResource).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"embedded prompt resource '{resourceName}' is missing — the build no longer embeds its prompts/ file");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
