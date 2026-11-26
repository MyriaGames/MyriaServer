using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems.World;
using Myria.Server.ClassicRealm.Data;
using Myria.Server.ClassicRealm.Models;

namespace Myria.Server.ClassicRealm.Services
{
    /// <summary>
    /// Owns the server's single live <see cref="WorldState"/> (NPC stock + room populations, v0.3
    /// quest rework): built from the current world data plus whatever was saved, then ticked and
    /// saved by <see cref="WorldTickService"/>. <see cref="State"/> is null until
    /// <see cref="LoadAsync"/> has run (startup, after the game data is loaded) - callers treat
    /// that as "no world state yet", i.e. everything available and nothing tracked.
    /// </summary>
    public class WorldStateService(IServiceScopeFactory scopeFactory, ILogger<WorldStateService> logger)
    {
        private readonly SemaphoreSlim _saveLock = new(1, 1);

        public WorldState? State { get; private set; }

        /// <summary>Builds the state from the loaded game data and the saved values. Must run after
        /// GameService.InitializeGame().</summary>
        public async Task LoadAsync()
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stock = await db.WorldNpcStock.AsNoTracking()
                .ToDictionaryAsync(s => new StockKey(s.RoomId, s.NpcId, s.ItemId), s => s.Quantity);
            var pops = await db.WorldRoomPopulations.AsNoTracking()
                .ToDictionaryAsync(p => new PopulationKey(p.RoomId, p.MonsterId), p => p.Count);

            State = new WorldState(
                WorldDerivation.DeriveNeeds(RoomService.AllRooms),
                WorldDerivation.DerivePopulations(RoomService.AllRooms),
                stock, pops);
            logger.LogInformation("World state loaded: {Stock} NPC stock entries, {Pops} room populations ({SavedStock}/{SavedPops} from save)",
                State.StockKeys.Count, State.PopulationKeys.Count, stock.Count, pops.Count);
        }

        /// <summary>Replaces the saved world state with the live one (full rewrite - a few hundred
        /// rows at most, written once per tick).</summary>
        public async Task SaveAsync()
        {
            if (State is null) return;
            var stock = State.SnapshotStock();
            var pops = State.SnapshotPopulation();

            await _saveLock.WaitAsync();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.WorldNpcStock.ExecuteDeleteAsync();
                await db.WorldRoomPopulations.ExecuteDeleteAsync();
                db.WorldNpcStock.AddRange(stock.Select(kv => new WorldNpcStock
                    { RoomId = kv.Key.RoomId, NpcId = kv.Key.NpcId, ItemId = kv.Key.ItemId, Quantity = kv.Value }));
                db.WorldRoomPopulations.AddRange(pops.Select(kv => new WorldRoomPopulation
                    { RoomId = kv.Key.RoomId, MonsterId = kv.Key.MonsterId, Count = kv.Value }));
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            finally
            {
                _saveLock.Release();
            }
        }
    }

    /// <summary>
    /// The world clock (v0.3 quest rework) - the server's first background service. Every
    /// <see cref="WorldRules.TickInterval"/>: populations grow, passively drained NPC stock shrinks,
    /// and the result is saved. Also saves once on shutdown so kills/sales since the last tick
    /// aren't lost on a clean stop.
    /// </summary>
    public class WorldTickService(WorldStateService world, ILogger<WorldTickService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(WorldRules.TickInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        world.State?.Tick();
                        await world.SaveAsync();
                    }
                    catch (Exception ex)
                    {
                        // One failed tick/save must never stop the world clock.
                        logger.LogError(ex, "World tick failed");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            try { await world.SaveAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Saving the world state on shutdown failed"); }
        }
    }
}
