// The suite was written for xunit, which creates a new instance of the test class for every test
// and runs test classes in parallel with each other. These two attributes give NUnit the same
// contract, so per-test state in constructors and fields stays per-test and a class that is safe
// to run beside another stays parallel.
[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[assembly: Parallelizable(ParallelScope.Fixtures)]
