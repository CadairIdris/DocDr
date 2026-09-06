using Xunit;

// PDFium native calls are serialised process-wide; running test collections in parallel adds
// no value here and makes host crashes harder to attribute. Keep the suite single-threaded.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
