using Xunit;

// Myria.Server.Realm depends on Myria.Lib.Core, which is built around static, process-wide
// services (GameService, ItemFactory, QuestManager, RoomService, ...). Myria.Lib.Tests disables
// parallelization for exactly this reason (see its AssemblyInfo.cs); the same hazard applies to
// every test here that loads a Character or resolves an item id, so it's disabled assembly-wide
// rather than chased down per test class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
