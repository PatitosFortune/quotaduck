using QuotaDuck.Bridge;

// TEST-ONLY entry point (see the csproj). Two behaviours the production Program deliberately does not have:
//   --print-production-state-dir : prints what the PRODUCTION resolver returns in THIS process (so tests can run it in a fresh
//                                  process with a hostile environment and compare);
//   QD_TEST_STATE_DIR            : an absolute directory to use instead. Without it this executable REFUSES to run (exit 3) so a
//                                  test can never accidentally read or write the real QuotaDuck state.
if (args.Length == 1 && args[0] == "--print-production-state-dir")
{
    Console.Out.Write(StateLocation.DefaultDirectory() ?? "(none)");
    return 0;
}

// Same, but FIRST let "another component in the process" do the ordinary lookup (Environment.GetFolderPath). The shell caches the
// result per process, so under a hostile environment that earlier call would poison a naive resolver's later lookups.
if (args.Length == 1 && args[0] == "--print-production-state-dir-after-plain-lookup")
{
    _ = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    Console.Out.Write(StateLocation.DefaultDirectory() ?? "(none)");
    return 0;
}

string? testDir = Environment.GetEnvironmentVariable("QD_TEST_STATE_DIR");
if (string.IsNullOrEmpty(testDir) || !Path.IsPathFullyQualified(testDir))
{
    return 3;
}

Stream stdin = Console.IsInputRedirected ? Console.OpenStandardInput() : Stream.Null;
IClaudeStateStore store = new FileClaudeStateStore(testDir);
using Stream stdout = Console.OpenStandardOutput();
return BridgeRunner.Run(args, stdin, stdout, store, static () => DateTimeOffset.UtcNow);
