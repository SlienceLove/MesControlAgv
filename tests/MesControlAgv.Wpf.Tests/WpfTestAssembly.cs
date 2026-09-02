using Xunit;

// WPF's BAML package loader and Dispatcher are process-wide resources. Running
// multiple STA window tests at once can produce nondeterministic package-stream
// failures even when each test owns its own dispatcher.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
