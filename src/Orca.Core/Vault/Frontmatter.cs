using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Orca.Core.Vault;

/// <summary>Splits a vault markdown file into YAML frontmatter and the opaque body. The
/// body is returned verbatim (line endings included) and is never parsed.</summary>
internal static class Frontmatter
{
    internal static (YamlMappingNode Root, string Body) Parse(string fileText, string filePath)
    {
        var (yamlText, body) = Split(fileText, filePath);

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yamlText));
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            // YamlDotNet surfaces duplicate mapping keys as ArgumentException, not YamlException.
            throw new VaultSchemaException(filePath, $"invalid YAML in frontmatter: {ex.Message}");
        }

        if (stream.Documents.Count != 1)
            throw new VaultSchemaException(filePath, "frontmatter must contain exactly one YAML document");
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new VaultSchemaException(filePath, "frontmatter must be a YAML mapping");

        return (root, body);
    }

    private static (string Yaml, string Body) Split(string fileText, string filePath)
    {
        // Hand-rolled line scan instead of a regex so the body survives byte-for-byte and
        // LF/CRLF files parse identically on both of the owner's machines.
        var pos = 0;
        var firstLine = NextLine(fileText, ref pos);
        if (firstLine is null || TrimCr(firstLine) != "---")
            throw new VaultSchemaException(filePath, "file must start with a '---' frontmatter delimiter");

        var yamlStart = pos;
        while (true)
        {
            var lineStart = pos;
            var line = NextLine(fileText, ref pos);
            if (line is null)
                throw new VaultSchemaException(filePath, "frontmatter is never closed by a '---' delimiter");

            if (TrimCr(line) == "---")
            {
                var yaml = fileText[yamlStart..lineStart];
                if (string.IsNullOrWhiteSpace(yaml))
                    throw new VaultSchemaException(filePath, "frontmatter is empty");
                return (yaml, fileText[pos..]);
            }
        }
    }

    private static string? NextLine(string text, ref int pos)
    {
        if (pos >= text.Length)
            return null;

        var newline = text.IndexOf('\n', pos);
        string line;
        if (newline < 0)
        {
            line = text[pos..];
            pos = text.Length;
        }
        else
        {
            line = text[pos..newline];
            pos = newline + 1;
        }

        return line;
    }

    private static string TrimCr(string line) => line.EndsWith('\r') ? line[..^1] : line;
}
