namespace QuotaDuck.Core;

/// <summary>Error-path helper shared by the allow-list parsers: classifies a JSON failure as "too deeply nested" with a cheap O(n)
/// structural scan (the reader's own messages are not reliable under resource-key trimming).</summary>
internal static class JsonDepthScan
{
    /// <summary>True if brackets nest deeper than <paramref name="maxDepth"/> outside of string literals.</summary>
    public static bool Exceeds(ReadOnlySpan<byte> utf8, int maxDepth)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        foreach (byte b in utf8)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            switch (b)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'{':
                case (byte)'[':
                    if (++depth > maxDepth)
                    {
                        return true;
                    }

                    break;
                case (byte)'}':
                case (byte)']':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
            }
        }

        return false;
    }
}
