using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Unicode;

namespace QuotaDuck.Core.Claude;

/// <summary>The whole persisted Claude state: at most two window records. Nothing else can be stored.</summary>
public sealed record ClaudeState(ClaudeWindowRecord? FiveHour, ClaudeWindowRecord? SevenDay)
{
    public static ClaudeState Empty { get; } = new(null, null);

    public ClaudeWindowRecord? Get(ClaudeWindowKind kind) => kind switch
    {
        ClaudeWindowKind.FiveHour => FiveHour,
        ClaudeWindowKind.SevenDay => SevenDay,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public ClaudeState With(ClaudeWindowKind kind, ClaudeWindowRecord? record) => kind switch
    {
        ClaudeWindowKind.FiveHour => this with { FiveHour = record },
        ClaudeWindowKind.SevenDay => this with { SevenDay = record },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public enum ClaudeStateDecodeStatus
{
    Ok = 0,

    /// <summary>Zero bytes (or nothing but a BOM): no state yet.</summary>
    Empty,

    /// <summary>Not valid UTF-8 / JSON / schema. The caller treats the state as empty and rewrites it.</summary>
    Corrupt,

    /// <summary>Larger than <see cref="ClaudeStateCodec.MaxBytes"/>: never read further; treated as corrupt.</summary>
    TooLarge,

    /// <summary>Written by a newer schema. MUST NOT be overwritten (would downgrade/clobber another version's data).</summary>
    FutureSchema,
}

public readonly record struct ClaudeStateDecodeResult(ClaudeStateDecodeStatus Status, ClaudeState State, int DroppedWindows);

/// <summary>
/// The persisted state schema (v1), exactly:
/// <code>
/// { "schema":1,
///   "fiveHour": { "usedPercent":23.5, "resetsAt":"2030-01-01T17:00:00.000Z"|null, "changedAt":"…Z", "receivedAt":"…Z",
///                 "lowerCandidate":null|number, "lowerStreak":0 } | null,
///   "sevenDay": { …same… } | null }
/// </code>
/// Times are UTC with millisecond precision. Maximum size <see cref="MaxBytes"/>. The record type cannot carry any other
/// field, so no payload content can reach the file. Decoding is strict and bounded; a window record that fails validation
/// is dropped on its own (the other window survives) and counted.
/// </summary>
public static class ClaudeStateCodec
{
    public const int SchemaVersion = 1;
    public const int MaxBytes = 4096;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly DateTimeOffset MinTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaxTime = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Canonical stored precision: UTC, truncated to whole milliseconds. Use for every instant that gets persisted.</summary>
    public static DateTimeOffset Canonicalize(DateTimeOffset t)
    {
        DateTimeOffset u = t.ToUniversalTime();
        return new DateTimeOffset(u.Ticks - (u.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public static byte[] Encode(ClaudeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", SchemaVersion);
            WriteWindow(w, "fiveHour", state.FiveHour);
            WriteWindow(w, "sevenDay", state.SevenDay);
            w.WriteEndObject();
        }

        byte[] bytes = buffer.WrittenSpan.ToArray();
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidOperationException("state exceeds maximum size");   // unreachable by construction
        }

        return bytes;
    }

    private static void WriteWindow(Utf8JsonWriter w, string name, ClaudeWindowRecord? r)
    {
        if (r is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteNumber("usedPercent", r.UsedPercent);
        if (r.ResetsAt is { } reset)
        {
            w.WriteString("resetsAt", Format(reset));
        }
        else
        {
            w.WriteNull("resetsAt");
        }

        w.WriteString("changedAt", Format(r.ChangedAt));
        w.WriteString("receivedAt", Format(r.ReceivedAt));
        if (r.LowerCandidate is { } lc)
        {
            w.WriteNumber("lowerCandidate", lc);
        }
        else
        {
            w.WriteNull("lowerCandidate");
        }

        w.WriteNumber("lowerStreak", r.LowerStreak);
        w.WriteEndObject();
    }

    private static string Format(DateTimeOffset t) => Canonicalize(t).UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static ClaudeStateDecodeResult Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes)
        {
            return new(ClaudeStateDecodeStatus.TooLarge, ClaudeState.Empty, 0);
        }

        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        if (utf8.Length == 0)
        {
            return new(ClaudeStateDecodeStatus.Empty, ClaudeState.Empty, 0);
        }

        if (!Utf8.IsValid(utf8))
        {
            return Corrupt();
        }

        var r = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 8 });
        try
        {
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
            {
                return Corrupt();
            }

            long? schema = null;
            ClaudeWindowRecord? five = null, seven = null;
            int dropped = 0;
            bool seenFive = false, seenSeven = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("schema"u8))
                {
                    if (schema is not null)
                    {
                        return Corrupt();
                    }

                    r.Read();
                    if (r.TokenType != JsonTokenType.Number || !r.TryGetInt64(out long s))
                    {
                        return Corrupt();
                    }

                    schema = s;
                }
                else if (r.ValueTextEquals("fiveHour"u8) || r.ValueTextEquals("sevenDay"u8))
                {
                    bool isFive = r.ValueTextEquals("fiveHour"u8);
                    if (isFive ? seenFive : seenSeven)
                    {
                        return Corrupt();
                    }

                    if (isFive)
                    {
                        seenFive = true;
                    }
                    else
                    {
                        seenSeven = true;
                    }

                    r.Read();
                    ClaudeWindowRecord? rec = ReadWindow(ref r, out bool bad);
                    if (bad)
                    {
                        dropped++;
                    }

                    if (isFive)
                    {
                        five = rec;
                    }
                    else
                    {
                        seven = rec;
                    }
                }
                else
                {
                    r.Read();
                    r.Skip();                  // unknown top-level field: ignored, never kept
                }
            }

            if (r.TokenType != JsonTokenType.EndObject || r.Read())
            {
                return Corrupt();
            }

            if (schema is null || schema < 1)
            {
                return Corrupt();
            }

            if (schema > SchemaVersion)
            {
                return new(ClaudeStateDecodeStatus.FutureSchema, ClaudeState.Empty, 0);
            }

            return new(ClaudeStateDecodeStatus.Ok, new ClaudeState(five, seven), dropped);
        }
        catch (JsonException)
        {
            return Corrupt();
        }
        catch (InvalidOperationException)
        {
            return Corrupt();
        }

        static ClaudeStateDecodeResult Corrupt() => new(ClaudeStateDecodeStatus.Corrupt, ClaudeState.Empty, 0);
    }

    private static ClaudeWindowRecord? ReadWindow(ref Utf8JsonReader r, out bool bad)
    {
        bad = false;
        if (r.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            bad = true;
            return null;
        }

        double? pct = null, lowerCandidate = null;
        DateTimeOffset? reset = null, changed = null, received = null;
        long streak = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            string name = r.GetString()!;
            r.Read();
            bool first = seen.Add(name);
            bool ok = true;
            switch (name)
            {
                case "usedPercent":
                    ok = r.TokenType == JsonTokenType.Number && r.TryGetDouble(out double d) && Within(d, 0, 100) && Assign(ref pct, d);
                    break;
                case "resetsAt":
                    if (r.TokenType == JsonTokenType.Null)
                    {
                        break;
                    }

                    ok = TryTime(ref r, out DateTimeOffset rt) && Assign(ref reset, rt);
                    break;
                case "changedAt":
                    ok = TryTime(ref r, out DateTimeOffset ct) && Assign(ref changed, ct);
                    break;
                case "receivedAt":
                    ok = TryTime(ref r, out DateTimeOffset at) && Assign(ref received, at);
                    break;
                case "lowerCandidate":
                    if (r.TokenType == JsonTokenType.Null)
                    {
                        break;
                    }

                    ok = r.TokenType == JsonTokenType.Number && r.TryGetDouble(out double lc) && Within(lc, 0, 100) && Assign(ref lowerCandidate, lc);
                    break;
                case "lowerStreak":
                    ok = r.TokenType == JsonTokenType.Number && r.TryGetInt64(out streak) && streak is >= 0 and <= 1000;
                    break;
                default:
                    break;                     // unknown window field: ignored, never kept
            }

            r.Skip();                          // no-op for scalars; steps over any nested container so the next token is a property name

            if (!first || !ok)
            {
                bad = true;
            }
        }

        if (pct is null || changed is null || received is null || changed > received
            || (streak > 0 && lowerCandidate is null))
        {
            bad = true;
        }

        return bad ? null : new ClaudeWindowRecord(pct!.Value, reset, changed!.Value, received!.Value, lowerCandidate, (int)streak);

        static bool Within(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
        static bool Assign<T>(ref T? slot, T value) where T : struct
        {
            slot = value;
            return true;
        }
    }

    private static bool TryTime(ref Utf8JsonReader r, out DateTimeOffset t)
    {
        t = default;
        if (r.TokenType != JsonTokenType.String)
        {
            return false;
        }

        string? s = r.GetString();
        if (s is null || !DateTimeOffset.TryParseExact(
                s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t))
        {
            return false;
        }

        return t >= MinTime && t < MaxTime;
    }
}
