using Xunit;

// WPF's pack-resource stream cache is process-wide, including tests that each
// create their own STA dispatcher. Concurrent fixture construction can race
// PackagePart.CleanUpRequestedStreamsList before a product assertion executes.
// Keep native-host fixtures serialized without changing their assertions.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
