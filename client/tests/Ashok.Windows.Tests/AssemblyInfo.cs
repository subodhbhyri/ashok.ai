using Xunit;

// The Desktop tests share one real desktop: they open, move, focus and close the same Settings and Explorer windows.
// Run in parallel, one test closes or steals the window another is using (and a timing check is skewed by the load).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
