using Xunit.Sdk;
using Xunit.v3;

// The SSH tests share one Pi account and its run lock (by design, only one script runs at a time), so the tests run
// one after another rather than in parallel.
[assembly: Parallelization(Mode = ParallelMode.None)]
