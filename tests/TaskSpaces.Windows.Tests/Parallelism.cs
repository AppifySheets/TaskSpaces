using Xunit;

// xunit runs separate test CLASSES in parallel by default, and that is wrong for this assembly.
//
// Found the moment a second class started constructing a FloatingBar: two of them built bars at
// the same time, on two STA threads, and both reached the same process-wide caches --
//
//   System.InvalidOperationException : Operations that change non-concurrent collections must have
//   exclusive access. A concurrent update was performed on this collection and corrupted its state.
//      at FloatingBar.LaneTint(Workspace workspace, Int32 index)
//
// -- with a matching intermittent failure inside IconCache. The symptom was a test that passed
// alone and failed in the suite, or failed in a DIFFERENT class each run, which is the signature
// worth recognising: neither test was wrong, they were standing on each other.
//
// Serialised here rather than by making those caches concurrent. They are reached only from the
// dispatcher thread in the real app -- one process, one UI thread, by construction -- so locking
// them would be paying real complexity in production to fix an artefact of the test runner. The
// runner is the thing that is wrong about the world, so the runner is what gets corrected.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
