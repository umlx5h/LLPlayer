using System.Text;

namespace FlyleafLib;

public static class SubtitleTextUtil
{
    /// <summary>
    /// Flattens the text into a single line.
    /// - If every line (excluding empty lines) starts with dash, returns the original string.
    /// - For all other text, replaces newlines with spaces and flattens into a single line.
    /// </summary>
    public static string FlattenText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        ReadOnlySpan<char> span = text.AsSpan();

        // If there are no newlines, return the text as-is
        if (!span.ContainsAny('\r', '\n'))
        {
            return text;
        }

        int length = span.Length;

        // Determine if the first character is dash to enter list mode
        bool startDash = span.Length > 0 && IsDash(span[0]);

        if (startDash)
        {
            char dashChar = span[0];

            // Check if all lines start with dash (ignore empty lines)
            bool allDash = true;
            bool atLineStart = true;
            int i;
            for (i = 0; i < length; i++)
            {
                if (atLineStart)
                {
                    // Skip empty lines
                    if (span[i] == '\r' || span[i] == '\n')
                    {
                        continue;
                    }
                    if (span[i] != dashChar)
                    {
                        allDash = false;
                        // Done checking
                        break;
                    }
                    atLineStart = false;
                }
                else
                {
                    if (span[i] == '\r')
                    {
                        if (i + 1 < length && span[i + 1] == '\n') i++;
                        atLineStart = true;
                    }
                    else if (span[i] == '\n')
                    {
                        atLineStart = true;
                    }
                }
            }

            // If every line starts with dash, return original text
            if (allDash)
            {
                return text;
            }

            // list mode
            StringBuilder sb = new(length);
            bool firstItem = true;
            i = 0;

            while (i < length)
            {
                int start;

                // Start of a dash line
                if (span[i] == dashChar)
                {
                    if (!firstItem)
                    {
                        sb.Append('\n');
                    }
                    // Append until end of line
                    start = i;
                    while (i < length && span[i] != '\r' && span[i] != '\n') i++;
                    sb.Append(span.Slice(start, i - start));
                    firstItem = false;
                    continue;
                }

                // Skip empty lines
                if (span[i] == '\r' || span[i] == '\n')
                {
                    i++;
                    continue;
                }

                // Continuation line
                start = i;
                while (i < length && span[i] != '\r' && span[i] != '\n') i++;
                sb.Append(' ');
                sb.Append(span.Slice(start, i - start));
            }
            return sb.ToString();
        }

        // Default mode: replace all newlines with spaces in one pass
        char[] buffer = new char[length];
        int pos = 0;
        bool lastWasNewline = false;
        for (int i = 0; i < length; i++)
        {
            char c = span[i];
            if (c == '\r' || c == '\n')
            {
                if (!lastWasNewline)
                {
                    buffer[pos++] = ' ';
                    lastWasNewline = true;
                }
            }
            else
            {
                buffer[pos++] = c;
                lastWasNewline = false;
            }
        }
        return new string(buffer, 0, pos);
    }

    private static bool IsDash(char c)
    {
        switch (c)
        {
            case '-':
            case '\u2043': // ⁃ Hyphen bullet
            case '\u2010': // ‐ Hyphen
            case '\u2012': // ‒ Figure dash
            case '\u2013': // – En dash
            case '\u2014': // — Em dash
            case '\u2015': // ― Horizontal bar
            case '\u2212': // − Minus Sign
                return true;
        }

        return false;
    }
}
