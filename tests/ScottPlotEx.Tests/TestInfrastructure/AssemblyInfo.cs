using Xunit;

// UI 测试会在真实窗体和消息循环中运行，串行执行可避免相互干扰。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
