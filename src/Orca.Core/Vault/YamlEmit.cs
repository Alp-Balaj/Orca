using System.Text;

namespace Orca.Core.Vault;

/// <summary>Shared emission helpers for the deterministic record serializers — one
/// quoting rule for every free-text string Orca writes into the vault.</summary>
internal static class YamlEmit
{
    internal static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case (char)0x2028:
                    sb.Append("\\u2028");
                    break;
                case (char)0x2029:
                    sb.Append("\\u2029");
                    break;
                default:
                    // C0 (U+0000-U+001F) and C1 (U+0080-U+009F, including the U+0085 NEL line
                    // break) controls are invalid unescaped in a YAML double-quoted scalar, and
                    // several of them read back as a line break, tripping the frontmatter
                    // scanner's indentation check.
                    if (c <= (char)0x1f || (c >= (char)0x80 && c <= (char)0x9f))
                        sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else
                        sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
