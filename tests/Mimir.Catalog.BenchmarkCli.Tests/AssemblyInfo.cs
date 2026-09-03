using Xunit;

// Console.Out/Console.Error are redirected by several contract tests that spawn
// or invoke child handlers; serialize them to avoid cross-talk.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
