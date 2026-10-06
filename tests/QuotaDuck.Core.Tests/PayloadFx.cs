using System.Text;

namespace QuotaDuck.Core.Tests;

/// <summary>
/// Synthetic Claude status-line payload builders. NEVER a real payload: every non-allow-listed value contains
/// <see cref="Canary"/>, so a test can prove none of it is retained, persisted or printed. Instants are anchored to a
/// fictional 2030 epoch (reset values just need to be inside the parser's 2000–2100 sanity window).
/// Linked into QuotaDuck.Bridge.Tests as well — keep it dependency-free.
/// </summary>
internal static class PayloadFx
{
    public const string Canary = "QD_CANARY_7c41e9";

    public static readonly DateTimeOffset Epoch = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static long Unix(DateTimeOffset t) => t.ToUnixTimeSeconds();

    /// <summary>A window object. Pass raw JSON fragments (so invalid types can be expressed); null omits the key.</summary>
    public static string Win(string? pct, string? reset)
    {
        var parts = new List<string>();
        if (pct is not null)
        {
            parts.Add("\"used_percentage\":" + pct);
        }

        if (reset is not null)
        {
            parts.Add("\"resets_at\":" + reset);
        }

        return "{" + string.Join(",", parts) + "}";
    }

    public static string Win(double pct, DateTimeOffset reset) =>
        Win(pct.ToString("R", System.Globalization.CultureInfo.InvariantCulture), Unix(reset).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A full realistic-shaped payload. <paramref name="five"/>/<paramref name="seven"/> are raw window JSON (null = key omitted).</summary>
    public static string Payload(string? five, string? seven, bool extras = true, string rateExtras = "", string rootExtras = "")
    {
        var rate = new List<string>();
        if (five is not null)
        {
            rate.Add("\"five_hour\":" + five);
        }

        if (seven is not null)
        {
            rate.Add("\"seven_day\":" + seven);
        }

        if (extras)
        {
            rate.Add("\"spend_limit\":{\"used_percentage\":1,\"resets_at\":1893456000,\"note\":\"" + Canary + "\"}");
        }

        if (rateExtras.Length > 0)
        {
            rate.Add(rateExtras);
        }

        var root = new List<string>();
        if (extras)
        {
            root.Add("\"session_id\":\"" + Canary + "-session\"");
            root.Add("\"transcript_path\":\"C:\\\\synthetic\\\\" + Canary + ".jsonl\"");
            root.Add("\"cwd\":\"C:\\\\synthetic\\\\" + Canary + "\"");
            root.Add("\"model\":{\"id\":\"" + Canary + "-model\",\"display_name\":\"" + Canary + "\"}");
            root.Add("\"workspace\":{\"current_dir\":\"C:\\\\synthetic\\\\" + Canary + "\",\"project_dir\":\"C:\\\\synthetic\"}");
            root.Add("\"cost\":{\"total_cost_usd\":0.12,\"total_duration_ms\":1234}");
            // DECOY: same key name as the allow-listed field but at a different path. Must never be read.
            root.Add("\"context_window\":{\"used_percentage\":8,\"resets_at\":1893456000,\"total_input_tokens\":1234}");
            root.Add("\"prompt_id\":\"" + Canary + "-prompt\"");
        }

        root.Add("\"rate_limits\":{" + string.Join(",", rate) + "}");
        if (rootExtras.Length > 0)
        {
            root.Add(rootExtras);
        }

        return "{" + string.Join(",", root) + "}";
    }

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>{"a":{"a":…1…}} with <paramref name="levels"/> object levels.</summary>
    public static string Nested(int levels) => new string('{', 0) + string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + new string('}', levels);

    public static string NestedArrays(int levels) => new string('[', levels) + "1" + new string(']', levels);
}
