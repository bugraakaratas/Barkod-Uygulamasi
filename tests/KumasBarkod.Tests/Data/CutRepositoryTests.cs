using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Services;

namespace KumasBarkod.Tests.Data;

public class CutRepositoryTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly CutRepository _repo;
    private readonly CutService _service;
    private readonly SteppingClock _clock = new();

    public CutRepositoryTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _repo = new CutRepository(_db.Factory);
        _service = new CutService(_db.Factory, new SettingsRepository(_db.Factory), _clock);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Empty_database_returns_empty_lists()
    {
        Assert.Empty(_repo.GetRecentLots());
        Assert.Empty(_repo.GetRecentCuts());
        Assert.Equal(0, _repo.CountPendingSync());
    }

    [Fact]
    public void Recent_lots_are_distinct_and_most_recent_first()
    {
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-B", 100);
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-C", 100);
        _service.CreateCut("LOT-B", 100);

        Assert.Equal(["LOT-B", "LOT-C", "LOT-A"], _repo.GetRecentLots());
    }

    [Fact]
    public void Recent_lots_respects_limit()
    {
        for (var i = 1; i <= 15; i++) _service.CreateCut($"LOT-{i:D2}", 100);

        var lots = _repo.GetRecentLots(limit: 10);
        Assert.Equal(10, lots.Count);
        Assert.Equal("LOT-15", lots[0]);
        Assert.Equal("LOT-06", lots[^1]);
    }

    [Fact]
    public void Recent_lots_with_same_timestamp_use_sequence_order()
    {
        _clock.Frozen = true;
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-B", 100);

        Assert.Equal(["LOT-B", "LOT-A"], _repo.GetRecentLots());
    }

    [Fact]
    public void Recent_cuts_are_newest_first()
    {
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-A", 200);
        _service.CreateCut("LOT-A", 300);

        var cuts = _repo.GetRecentCuts(limit: 2);
        Assert.Equal(["K1-000003", "K1-000002"], cuts.Select(c => c.CutId));
        Assert.Equal(300, cuts[0].LengthCm);
    }

    [Fact]
    public void Pending_count_counts_unsent_cuts_only()
    {
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-A", 100);
        Assert.Equal(3, _repo.CountPendingSync());

        using (var c = _db.Factory.Open())
            c.Execute("UPDATE sync_outbox SET sent_at = '2026-11-01T10:00:00.000Z' WHERE cut_id = 'K1-000001'");

        Assert.Equal(2, _repo.CountPendingSync());
    }

    /// <summary>Her çağrıda 1 sn ilerleyen saat (Frozen ise sabit).</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        public bool Frozen { get; set; }
        public override DateTimeOffset GetUtcNow() => Frozen ? _now : (_now = _now.AddSeconds(1));
    }
}
