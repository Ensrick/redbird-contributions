using Xunit;

// Every test patches executable memory in this process. Running classes in parallel means one test can be reading back the bytes of its target while another is mid-write somewhere else in
// the same slab, and the shared near-allocator hands out from one bump region. The suite is small enough that serial execution costs little and removes a whole category of phantom failures.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
