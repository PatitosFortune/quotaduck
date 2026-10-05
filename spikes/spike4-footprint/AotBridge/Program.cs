// SPIKE 4 prototype of the status-line bridge workload (NOT the production bridge): bounded stdin read,
// allow-list streaming parse of rate_limits.{five_hour,seven_day}.{used_percentage,resets_at}, atomic file write.
// Everything else in the payload is skipped, never stored. Output dir from QD_SPIKE_OUT (scratch).
using System.Text;
using System.Text.Json;

const int MaxBytes = 256 * 1024;
bool tap = args.Contains("--tap");
string outDir = Environment.GetEnvironmentVariable("QD_SPIKE_OUT") ?? Path.Combine(Path.GetTempPath(), "qd-spike4-bridge");

byte[] buf;
using (var stdin = Console.OpenStandardInput())
using (var ms = new MemoryStream())
{
    var chunk = new byte[8192]; int n;
    while ((n = stdin.Read(chunk, 0, chunk.Length)) > 0) { if (ms.Length + n > MaxBytes) return 0; ms.Write(chunk, 0, n); }
    buf = ms.ToArray();
}

(double? pct, long? reset) five = default, seven = default;
try
{
    var r = new Utf8JsonReader(buf, new JsonReaderOptions { MaxDepth = 32 });
    if (r.Read() && r.TokenType == JsonTokenType.StartObject)
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("rate_limits"u8) && r.Read() && r.TokenType == JsonTokenType.StartObject)
            {
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    bool f = r.ValueTextEquals("five_hour"u8), s = r.ValueTextEquals("seven_day"u8);
                    if ((f || s) && r.Read() && r.TokenType == JsonTokenType.StartObject)
                    {
                        double? p = null; long? rs = null;
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            if (r.ValueTextEquals("used_percentage"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number && r.TryGetDouble(out var d) && d is >= 0 and <= 100) p = d; else r.Skip(); }
                            else if (r.ValueTextEquals("resets_at"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number && r.TryGetInt64(out var l)) rs = l; else r.Skip(); }
                            else { r.Read(); r.Skip(); }
                        }
                        if (f) five = (p, rs); else seven = (p, rs);
                    }
                    else { r.Read(); r.Skip(); }
                }
            }
            else { r.Read(); r.Skip(); }
        }
}
catch (JsonException) { return 0; }

Directory.CreateDirectory(outDir);
string tmp = Path.Combine(outDir, "state.json." + Environment.ProcessId + ".tmp"), dst = Path.Combine(outDir, "state.json");
using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
using (var w = new Utf8JsonWriter(fs))
{
    w.WriteStartObject(); w.WriteNumber("schema", 1); w.WriteString("receivedAtUtc", DateTime.UtcNow);
    foreach (var (name, v) in new[] { ("fiveHour", five), ("sevenDay", seven) })
    { w.WriteStartObject(name); if (v.pct is { } p) w.WriteNumber("usedPercent", p); if (v.reset is { } rs) w.WriteNumber("resetsAtUnix", rs); w.WriteEndObject(); }
    w.WriteEndObject();
}
File.Move(tmp, dst, overwrite: true);
if (!tap) Console.WriteLine(five.pct is { } q ? $"QuotaDuck | Claude 5h {q:0.#}%" : "QuotaDuck | Claude usage unavailable");
return 0;
