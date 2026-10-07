using System.Text.Json;

namespace QuotaDuck.Coordinator;

/// <summary>What the user-level Claude Code settings say about a status line. Nothing else about the settings is ever read or kept.</summary>
public enum ClaudeSetupState
{
    /// <summary>The settings could not be read or understood (missing permissions, too large, not JSON). Treated like "not set up", without claiming it.</summary>
    Unknown = 0,

    /// <summary>No <c>statusLine</c> entry (or no settings file): nothing is wired to QuotaDuck's bridge.</summary>
    NotConfigured = 1,

    /// <summary>The <c>statusLine</c> command is QuotaDuck's bridge.</summary>
    BridgeConfigured = 2,

    /// <summary>Another status-line command exists: it must NOT be replaced; the bridge's tap mode is the way to add QuotaDuck.</summary>
    OtherStatusLine = 3,
}

public interface IClaudeSetupProbe
{
    ClaudeSetupState Check();
}

/// <summary>
/// A narrow, read-only look at the user-level Claude Code settings file: is there a top-level <c>statusLine</c> entry, and is its command
/// QuotaDuck's bridge? The file is streamed once (bounded to 256 KiB), every other property is skipped unread, the command text is compared
/// in place and never copied, stored or exposed — the result is a four-value enum. Project-level settings are not looked at. Nothing is written.
/// </summary>
internal sealed class ClaudeSetupProbe(string? settingsPath) : IClaudeSetupProbe
{
    public const int MaxBytes = 256 * 1024;
    private static readonly byte[] BridgeName = "quotaduck-statusline"u8.ToArray();

    public static string? DefaultPath()
    {
        string? profile = ClaudeStateLocation.UserProfileDirectory();
        return profile is null ? null : Path.Combine(profile, ".claude", "settings.json");
    }

    public ClaudeSetupState Check()
    {
        if (settingsPath is null)
        {
            return ClaudeSetupState.Unknown;
        }

        byte[] buffer;
        int length = 0;
        try
        {
            using var fs = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            buffer = new byte[MaxBytes + 1];
            int n;
            while (length < buffer.Length && (n = fs.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += n;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClaudeSetupState.NotConfigured;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ClaudeSetupState.Unknown;
        }

        if (length > MaxBytes)
        {
            return ClaudeSetupState.Unknown;
        }

        ReadOnlySpan<byte> json = buffer.AsSpan(0, length);
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            json = json[3..];
        }

        if (json.IndexOfAnyExcept(" \t\r\n"u8) < 0)
        {
            return ClaudeSetupState.NotConfigured;                  // an empty settings file sets nothing
        }

        try
        {
            return Classify(json);
        }
        catch (JsonException)
        {
            return ClaudeSetupState.Unknown;
        }
        catch (InvalidOperationException)
        {
            return ClaudeSetupState.Unknown;
        }
    }

    internal static ClaudeSetupState Classify(ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 });
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
        {
            return ClaudeSetupState.Unknown;
        }

        ClaudeSetupState result = ClaudeSetupState.NotConfigured;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (!r.ValueTextEquals("statusLine"u8))
            {
                r.Read();
                r.Skip();                                           // every other setting is skipped token by token, unread
                continue;
            }

            r.Read();
            if (r.TokenType != JsonTokenType.StartObject)
            {
                result = ClaudeSetupState.OtherStatusLine;          // e.g. a string/null/array: something is there, and it is not ours
                r.Skip();
                continue;
            }

            ClaudeSetupState entry = ClaudeSetupState.OtherStatusLine;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("command"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.String && ContainsIgnoreCase(r.ValueSpan, BridgeName))
                    {
                        entry = ClaudeSetupState.BridgeConfigured;
                    }

                    r.Skip();
                }
                else
                {
                    r.Read();
                    r.Skip();
                }
            }

            result = entry;                                         // last "statusLine" wins, like a normal JSON read
        }

        return result;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, byte[] needleLower)
    {
        for (int i = 0; i + needleLower.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needleLower.Length && match; j++)
            {
                byte c = haystack[i + j];
                if (c is >= (byte)'A' and <= (byte)'Z')
                {
                    c = (byte)(c + 32);
                }

                match = c == needleLower[j];
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
