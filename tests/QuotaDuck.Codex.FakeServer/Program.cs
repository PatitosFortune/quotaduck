// TEST-ONLY fake `codex app-server`. Never shipped, never referenced by production code. It speaks (or deliberately misspeaks)
// newline-delimited JSON-RPC on stdio so the runner's process handling can be tested without ever starting the real Codex.
// Behaviour is selected by arguments the production launch spec never contains:
//   --qd-scenario NAME   --qd-payload FILE (bytes sent verbatim as the rateLimits reply)
//   --qd-pre FILE (bytes sent just before the initialize reply)   --qd-record FILE (diagnostics + raw stdin bytes)
using System.Diagnostics;
using System.Text;

string scenario = "normal", payloadFile = "", preFile = "", recordFile = "";
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--qd-scenario": scenario = args[i + 1]; break;
        case "--qd-payload": payloadFile = args[i + 1]; break;
        case "--qd-pre": preFile = args[i + 1]; break;
        case "--qd-record": recordFile = args[i + 1]; break;
    }
}

Stream stdin = Console.OpenStandardInput();
Stream stdout = Console.OpenStandardOutput();
Stream stderr = Console.OpenStandardError();
byte[] defaultPayload = Encoding.UTF8.GetBytes(Fixture.GoodReply(2) + "\n");

void Rec(string line)
{
    if (recordFile.Length > 0) File.AppendAllText(recordFile, line + "\n", new UTF8Encoding(false));
}

void RecRaw(byte[] bytes)
{
    if (recordFile.Length > 0)
    {
        using var f = new FileStream(recordFile + ".stdin", FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        f.Write(bytes);
    }
}

void Send(byte[] bytes) { stdout.Write(bytes); stdout.Flush(); }
void SendText(string s) => Send(Encoding.UTF8.GetBytes(s));

byte[]? ReadLine()
{
    var ms = new MemoryStream();
    int b;
    while ((b = stdin.ReadByte()) >= 0)
    {
        ms.WriteByte((byte)b);
        if (b == '\n') break;
    }

    if (ms.Length == 0) return null;
    byte[] raw = ms.ToArray();
    RecRaw(raw);
    return raw;
}

Rec("PID:" + Environment.ProcessId);

if (scenario == "echo-env")
{
    foreach (string a in args) Rec("ARG:" + a);
    foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) Rec("ENV:" + e.Key);
    Rec("SYSROOT:" + Environment.GetEnvironmentVariable("SystemRoot"));
    Rec("CWD:" + Environment.CurrentDirectory);
}

if (scenario == "exit-on-start") return 7;
if (scenario == "sleep") { Thread.Sleep(Timeout.Infinite); }
if (scenario == "stderr-noise") { stderr.Write(new byte[1_000_000]); stderr.Flush(); }
if (scenario == "stderr-canary") { stderr.Write(Encoding.UTF8.GetBytes("CANARY_STDERR_SECRET\n")); stderr.Flush(); }

if (scenario == "hang-with-child")
{
    var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [Environment.GetCommandLineArgs()[0], "--qd-scenario", "sleep", "--qd-record", recordFile + ".child"]) { UseShellExecute = false, CreateNoWindow = true })!;
    Rec("CHILDPID:" + child.Id);
    Thread.Sleep(Timeout.Infinite);
}

bool inited = false;
while (true)
{
    byte[]? raw = ReadLine();
    if (raw is null)
    {
        Rec("EOF-ON-STDIN");
        if (scenario == "ignore-eof") Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    string line = Encoding.UTF8.GetString(raw);
    if (line.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
    {
        if (scenario == "silent-init") continue;
        if (scenario == "init-error") { SendText("{\"id\":1,\"error\":{\"code\":-32600,\"message\":\"CANARY_INIT_MESSAGE\"}}\n"); continue; }
        if (preFile.Length > 0) Send(File.ReadAllBytes(preFile));
        SendText("{\"id\":1,\"result\":{\"userAgent\":\"fake/0\",\"codexHome\":\"C:\\\\CANARY_HOME\"}}\n");
        inited = true;
        if (scenario == "unexpected-request") SendText("{\"id\":99,\"method\":\"item/tool/call\",\"params\":{\"x\":\"CANARY_PARAMS\"}}\n");
        if (scenario == "init-wrong-id") SendText("{\"id\":55,\"result\":{}}\n");
    }
    else if (line.Contains("\"method\":\"account/rateLimits/read\"", StringComparison.Ordinal))
    {
        if (!inited) { Rec("READ-BEFORE-INIT"); }
        switch (scenario)
        {
            case "silent-read": break;
            case "crash-after-init": return 9;
            case "partial-then-exit": SendText("{\"id\":2,\"result\":{\"rateLimits\":"); return 0;
            case "oversize": Send(new byte[3 * 1024 * 1024].Select(_ => (byte)'a').ToArray()); Thread.Sleep(Timeout.Infinite); break;
            case "wrong-id": SendText(Fixture.GoodReply(77) + "\n"); break;
            case "error-reply": SendText("{\"id\":2,\"error\":{\"code\":-32600,\"message\":\"CANARY_ERROR_MESSAGE\"}}\n"); break;
            case "fragment":
                for (int k = 0; k < defaultPayload.Length; k++) { stdout.WriteByte(defaultPayload[k]); stdout.Flush(); if (k == defaultPayload.Length / 2) Thread.Sleep(150); }
                break;
            case "flood": for (int i = 0; i < 1000; i++) SendText("{\"method\":\"thread/started\",\"params\":{}}\n"); break;
            default: Send(payloadFile.Length > 0 ? File.ReadAllBytes(payloadFile) : defaultPayload); break;
        }
    }
}

internal static class Fixture
{
    // Synthetic, fictional epoch (2030-01-01 = 1893456000). Every field QuotaDuck must discard carries a CANARY_ value.
    public static string Bucket(string id) =>
        "{\"limitId\":\"" + id + "\",\"limitName\":null," +
        "\"primary\":{\"usedPercent\":42,\"windowDurationMins\":300,\"resetsAt\":1893456000}," +
        "\"secondary\":{\"usedPercent\":16,\"windowDurationMins\":10080,\"resetsAt\":1893974400}," +
        "\"credits\":{\"hasCredits\":true,\"unlimited\":false,\"balance\":\"CANARY_BALANCE\"}," +
        "\"planType\":\"CANARY_PLAN\",\"rateLimitReachedType\":null}";

    public static string GoodReply(int id) =>
        "{\"id\":" + id + ",\"result\":{\"rateLimits\":" + Bucket("codex") + ",\"rateLimitsByLimitId\":{\"codex\":" + Bucket("codex") + "}," +
        "\"accountId\":\"CANARY_ACCOUNT\",\"rateLimitResetCredits\":{\"grants\":[{\"id\":\"CANARY_GRANT\"}]}," +
        "\"rateLimitUpsell\":{\"url\":\"CANARY_URL\",\"referral\":\"CANARY_REFERRAL\"}}}";
}
