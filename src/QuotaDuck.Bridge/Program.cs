using QuotaDuck.Bridge;

// quotaduck-statusline [--tap] [--label=<text>]
//   normal: reads Claude Code's status-line JSON on stdin, updates QuotaDuck's state, prints ONE short line.
//   --tap : same, prints nothing (for adding to an existing status-line script: `... | quotaduck-statusline --tap`).
// The state location is fixed (see StateLocation); there is no path argument and no environment override.
Stream stdin = Console.IsInputRedirected ? Console.OpenStandardInput() : Stream.Null;   // never block waiting on a console
IClaudeStateStore store = new DeferredStateStore(StateLocation.DefaultDirectory);
using Stream stdout = Console.OpenStandardOutput();
return BridgeRunner.Run(args, stdin, stdout, store, static () => DateTimeOffset.UtcNow);
