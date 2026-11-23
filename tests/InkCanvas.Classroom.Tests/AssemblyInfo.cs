using Xunit;

// Classroom 套件包含压测/吞吐夹具，整体串行执行，避免并行干扰计时与临时目录。
[assembly: CollectionBehavior(DisableTestParallelization = true)]