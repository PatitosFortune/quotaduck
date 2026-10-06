using Xunit;

// Some tests deliberately mutate PROCESS-WIDE state (hostile environment variables, the named mutex, the shell's per-process folder
// cache). Running them alongside other tests would make results depend on scheduling (and did: a parallel test once created its temp
// folder under a hostile TEMP). The whole assembly therefore runs sequentially; it takes seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
