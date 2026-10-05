// Harmless scratch child: reports which copy of itself actually ran.
Console.WriteLine("CHILD_PATH=" + Environment.ProcessPath);
if (args.Length > 0) Console.WriteLine("CHILD_ARGS=" + string.Join("|", args));
