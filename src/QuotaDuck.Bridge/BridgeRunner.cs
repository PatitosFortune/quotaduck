using System.Text;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.Bridge;

/// <summary>
/// stdin JSON -> allow-listed quota fields (Core parser) -> Core merge -> atomic state -> optional one-line stdout.
/// Never throws and never blocks the status line: every runtime failure degrades to "no write" with exit code 0.
/// Exit code 2 is reserved for invalid arguments (nothing is read or written then).
/// </summary>
internal static class BridgeRunner
{
    private const int MaxDrainBytes = 4 * 1024 * 1024;

    public static int Run(
        string[] args, Stream stdin, Stream stdout, IClaudeStateStore? store, Func<DateTimeOffset> clock, FreshnessPolicy? policy = null)
    {
        if (!TryParseArgs(args, out bool tap, out string label))
        {
            return 2;
        }

        try
        {
            policy ??= FreshnessPolicy.Default;
            DateTimeOffset now = ClaudeStateCodec.Canonicalize(clock());
            ClaudeStatusLineParse parse = ReadAndParse(stdin);
            ClaudeIncomingWindow? five = parse.Status == ClaudeParseStatus.Ok ? parse.FiveHour.ToIncoming() : null;
            ClaudeIncomingWindow? seven = parse.Status == ClaudeParseStatus.Ok ? parse.SevenDay.ToIncoming() : null;
            bool hasUsable = five is not null || seven is not null;

            if (tap && !hasUsable)
            {
                return 0;                      // nothing to do and nothing to print: do not even touch the disk
            }

            ClaudeState shown = ClaudeState.Empty;
            if (store is not null)
            {
                if (hasUsable)
                {
                    StoreOutcome outcome = store.Update(current => Merge(current, five, seven, now, policy));
                    // If the update could not run (lock timeout / I/O error / newer schema) show what is actually on disk,
                    // never an empty "waiting" line that would hide valid earlier state.
                    shown = outcome.Status == StoreStatus.Ok ? outcome.Final : store.Peek();
                }
                else
                {
                    shown = store.Peek();
                }
            }

            if (!tap)
            {
                byte[] line = Encoding.UTF8.GetBytes(ClaudeStatusLineFormatter.Format(shown, now, policy, label) + "\n");
                stdout.Write(line, 0, line.Length);
                stdout.Flush();
            }
        }
        catch (Exception)
        {
            // The status line must never fail loudly: no output, no logging of anything.
        }

        return 0;
    }

    private static StateChange Merge(ClaudeState current, ClaudeIncomingWindow? five, ClaudeIncomingWindow? seven, DateTimeOffset now, FreshnessPolicy policy)
    {
        ClaudeMergeResult f = ClaudeWindowMerger.Merge(ClaudeWindowKind.FiveHour, current.FiveHour, five, now, policy);
        ClaudeMergeResult s = ClaudeWindowMerger.Merge(ClaudeWindowKind.SevenDay, current.SevenDay, seven, now, policy);
        return new StateChange(new ClaudeState(f.Record, s.Record), f.ShouldPersist || s.ShouldPersist);
    }

    private static bool TryParseArgs(string[] args, out bool tap, out string label)
    {
        tap = false;
        label = ClaudeStatusLineFormatter.DefaultLabel;
        bool labelSeen = false;
        foreach (string a in args)
        {
            if (a == "--tap" && !tap)
            {
                tap = true;
            }
            else if (a.StartsWith("--label=", StringComparison.Ordinal) && !labelSeen
                     && ClaudeStatusLineFormatter.TrySanitizeLabel(a["--label=".Length..], out string l))
            {
                label = l;
                labelSeen = true;
            }
            else
            {
                return false;                  // unknown, duplicate or unsafe argument
            }
        }

        return true;
    }

    private static ClaudeStatusLineParse ReadAndParse(Stream stdin)
    {
        // Bounded read: never buffers more than the parser's limit + 1 byte; an oversized payload is drained (discarded)
        // so Claude Code does not see a broken pipe, then rejected.
        byte[] buffer = new byte[16 * 1024];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > ClaudeStatusLineParser.MaxPayloadBytes)
                {
                    Drain(stdin);
                    return new ClaudeStatusLineParse(ClaudeParseStatus.TooLarge, default, default);
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, ClaudeStatusLineParser.MaxPayloadBytes + 1));
            }

            int n = stdin.Read(buffer, length, buffer.Length - length);
            if (n <= 0)
            {
                break;
            }

            length += n;
        }

        return ClaudeStatusLineParser.Parse(buffer.AsSpan(0, length));
    }

    private static void Drain(Stream stdin)
    {
        byte[] sink = new byte[8192];
        long total = 0;
        while (total < MaxDrainBytes)
        {
            int n = stdin.Read(sink, 0, sink.Length);
            if (n <= 0)
            {
                return;
            }

            total += n;
        }
    }
}
