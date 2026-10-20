using NetDeamon.apps.HeatingTest;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// HeatingTest app: when a test/reference night starts, ends or aborts (HeatingTestLogic), and its
/// SQLite log (HeatingTestDb, against a temporary file). The app class itself only reads HA
/// entities and calls number.set_value, which needs a live Home Assistant.
/// </summary>
public class HeatingTestTests : TestBase
{
  static readonly DateTime Start = new(2026, 11, 20, 22, 0, 0);
  static readonly TestSettings Armed = new(true, new TimeOnly(22, 0), 12, -3);
  static readonly RoomReading BadOk = new("Bad", 20.8, 21.0, 0.5);

  static TestInput Input(DateTime? now = null, TestSettings? settings = null, HeatingRun? active = null, RunKind? lastKind = null,
    DateOnly? lastAttempt = null, double? outdoor = 2, int heatingMinutes = 600, IReadOnlyList<RoomReading>? rooms = null) =>
    new(now ?? Start, settings ?? Armed, active, lastKind, lastAttempt, outdoor, heatingMinutes, rooms ?? [BadOk], 6, 120);

  static readonly HeatingRun TestRun = new(1, RunKind.Test, Start, Start.AddHours(12), -3, -1.6);

  // ── Start ──────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public void FirstNight_StartsATest()
  {
    var d = HeatingTestLogic.Decide(Input());
    Assert.Equal(TestAction.Start, d.Action);
    Assert.Equal(RunKind.Test, d.Kind);
  }

  [Fact]
  public void AlternatesTestAndReference()
  {
    Assert.Equal(RunKind.Reference, HeatingTestLogic.Decide(Input(lastKind: RunKind.Test)).Kind);
    Assert.Equal(RunKind.Test, HeatingTestLogic.Decide(Input(lastKind: RunKind.Reference)).Kind);
  }

  [Fact]
  public void NotArmed_NeverStarts() =>
    Assert.Equal(TestAction.None, HeatingTestLogic.Decide(Input(settings: Armed with { Armed = false })).Action);

  [Fact]
  public void OnlyWithinTheStartWindow_OncePerDay()
  {
    Assert.Equal(TestAction.None, HeatingTestLogic.Decide(Input(now: Start.AddMinutes(-1))).Action);
    Assert.Equal(TestAction.Start, HeatingTestLogic.Decide(Input(now: Start.AddMinutes(9))).Action);
    // E.g. a restart at 03:00 doesn't start a night late.
    Assert.Equal(TestAction.None, HeatingTestLogic.Decide(Input(now: Start.AddMinutes(HeatingTestLogic.StartWindowMinutes))).Action);
    Assert.Equal(TestAction.None, HeatingTestLogic.Decide(Input(lastAttempt: DateOnly.FromDateTime(Start))).Action);
  }

  [Fact]
  public void HeatPumpNotHeatingYet_Skips()
  {
    var d = HeatingTestLogic.Decide(Input(heatingMinutes: 30));
    Assert.Equal(TestAction.Skip, d.Action);
    Assert.Contains("30 min", d.Reason);
  }

  [Fact]
  public void HeatPumpNotInHeatingMode_Skips()
  {
    // Live 2026-10-20: select.warmepumpe_modus = "Nur Warmwasser" — heating not switched on yet.
    var d = HeatingTestLogic.Decide(Input() with { HeatingMode = "Nur Warmwasser", HeatingModeOn = false });
    Assert.Equal(TestAction.Skip, d.Action);
    Assert.Contains("Nur Warmwasser", d.Reason);
  }

  [Fact]
  public void HeatPumpLeavesHeatingMode_EndsTheRun()
  {
    var d = HeatingTestLogic.Decide(Input(now: Start.AddHours(2), active: TestRun) with { HeatingMode = "Aus", HeatingModeOn = false });
    Assert.Equal(TestAction.End, d.Action);
    Assert.Contains("Aus", d.Reason);
  }

  [Fact]
  public void TooWarmOutside_Skips() =>
    Assert.Equal(TestAction.Skip, HeatingTestLogic.Decide(Input(outdoor: 8)).Action);

  // ── Running ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public void Running_EndsAtItsPlannedEnd()
  {
    Assert.Equal(TestAction.None, HeatingTestLogic.Decide(Input(now: Start.AddHours(11.9), active: TestRun)).Action);
    var d = HeatingTestLogic.Decide(Input(now: Start.AddHours(12), active: TestRun));
    Assert.Equal(TestAction.End, d.Action);
    Assert.Equal("completed", d.Reason);
  }

  [Fact]
  public void Disarmed_EndsRightAway()
  {
    var d = HeatingTestLogic.Decide(Input(now: Start.AddHours(2), active: TestRun, settings: Armed with { Armed = false }));
    Assert.Equal(TestAction.End, d.Action);
    Assert.Equal("disarmed", d.Reason);
  }

  [Fact]
  public void RoomTooCold_AbortsTheTest()
  {
    var cold = new RoomReading("Bad", 20.4, 21.0, 0.5);
    var d = HeatingTestLogic.Decide(Input(now: Start.AddHours(5), active: TestRun, rooms: [cold]));
    Assert.Equal(TestAction.End, d.Action);
    Assert.Contains("Bad", d.Reason);
  }

  [Fact]
  public void RoomWithoutAbortMargin_OnlyLogged()
  {
    var keller = new RoomReading("Keller", 12, 17, null);
    Assert.Null(HeatingTestLogic.TooCold([keller, BadOk]));
  }

  [Fact]
  public void ReferenceRun_NotAbortedByCold()
  {
    // Nothing was changed, so a cold room isn't the test's doing.
    var cold = new RoomReading("Bad", 19.0, 21.0, 0.5);
    var reference = TestRun with { Kind = RunKind.Reference, OriginalLevel = null };
    Assert.Equal(TestAction.None, HeatingTestLogic.Decide(Input(now: Start.AddHours(5), active: reference, rooms: [cold])).Action);
  }

  [Theory]
  [InlineData(-1.6, -3, -4.6)]
  [InlineData(-14, -3, -15)]
  public void TestLevel_OffsetWithinTheHeatPumpsRange(double original, double offset, double expected) =>
    Assert.Equal(expected, HeatingTestLogic.TestLevel(original, offset));

  // ── DB ─────────────────────────────────────────────────────────────────────────────────────

  static async Task<(HeatingTestDb Db, string Path)> NewDb()
  {
    string path = Path.Combine(Path.GetTempPath(), $"heating_test_{Guid.NewGuid():N}.db");
    var db = new HeatingTestDb(path);
    await db.InitializeAsync();
    return (db, path);
  }

  [Fact]
  public async Task Db_RunSurvivesRestartUntilEndedAndRestored()
  {
    var (db, path) = await NewDb();
    try
    {
      long id = await db.StartRunAsync(RunKind.Test, Start, Start.AddHours(12), -3, -1.6, 2.5);

      // "Restart": a new instance on the same file finds the running test and its original level.
      var reopened = new HeatingTestDb(path);
      var active = await reopened.ActiveRunAsync();
      Assert.NotNull(active);
      Assert.Equal((id, RunKind.Test, Start, -1.6), (active!.Id, active.Kind, active.Start, active.OriginalLevel!.Value));

      await reopened.EndRunAsync(id, Start.AddHours(3), "aborted: Bad");
      Assert.Null(await reopened.ActiveRunAsync());
      Assert.Equal(id, (await reopened.UnrestoredTestAsync())!.Id);

      await reopened.MarkRestoredAsync(id);
      Assert.Null(await reopened.UnrestoredTestAsync());
      var recent = Assert.Single(await reopened.RecentRunsAsync(5));
      Assert.Equal("aborted: Bad", recent.Reason);
    }
    finally { File.Delete(path); }
  }

  [Fact]
  public async Task Db_FirstDeployedSchema_MigratedOnStart()
  {
    // Live 2026-10-20 13:09: the first deployed version created samples without hp_mode; the next
    // version's insert failed with "table samples has no column named hp_mode".
    string path = Path.Combine(Path.GetTempPath(), $"heating_test_{Guid.NewGuid():N}.db");
    try
    {
      await using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
      {
        await con.OpenAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = """
          CREATE TABLE samples (ts TEXT NOT NULL, run_id INTEGER, phase TEXT NOT NULL, outdoor REAL, outdoor_house REAL,
            flow REAL, flow_setpoint REAL, return_temp REAL, compressor REAL, power REAL, cop REAL,
            level REAL, slope REAL, heating INTEGER, warmwater INTEGER);
          INSERT INTO samples (ts, phase) VALUES ('2026-10-20T13:00:00+02:00', 'Normal');
          """;
        await cmd.ExecuteNonQueryAsync();
      }

      var db = new HeatingTestDb(path);
      await db.InitializeAsync();
      await db.InsertSampleAsync(new HeatPumpSample(new DateTime(2026, 10, 20, 13, 10, 0), null, "Normal",
        11, 11, 27, 0, 22, 0, 40, 0, -1.6, 0.1, false, false, "Nur Warmwasser"), []);
      await db.InitializeAsync(); // a second start changes nothing

      await using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
      await check.OpenAsync();
      await using var q = check.CreateCommand();
      q.CommandText = "SELECT COUNT(*), MAX(hp_mode), (SELECT user_version FROM pragma_user_version) FROM samples";
      await using var r = await q.ExecuteReaderAsync();
      Assert.True(await r.ReadAsync());
      Assert.Equal(2, r.GetInt64(0));                 // old row kept
      Assert.Equal("Nur Warmwasser", r.GetString(1));
      Assert.Equal(HeatingTestDb.SchemaVersion, r.GetInt32(2));
    }
    finally { File.Delete(path); }
  }

  [Fact]
  public async Task Db_HeatingMinutesCountOnlyHeatingWithCompressor()
  {
    var (db, path) = await NewDb();
    try
    {
      HeatPumpSample Sample(DateTime t, bool heating, double compressor) =>
        new(t, null, "Normal", 2, 3, 27, 27, 23, compressor, 600, 4.5, -1.6, 0.1, heating, false);
      var t0 = Start.AddHours(-3);
      for (int i = 0; i < 60; i++) await db.InsertSampleAsync(Sample(t0.AddMinutes(i), true, 30), [new RoomSample("Bad", 21, 21, "heating", "comfort")]);
      for (int i = 60; i < 90; i++) await db.InsertSampleAsync(Sample(t0.AddMinutes(i), true, 0), []);   // flag on, compressor off
      for (int i = 90; i < 120; i++) await db.InsertSampleAsync(Sample(t0.AddMinutes(i), false, 30), []); // warm water

      Assert.Equal(60, await db.HeatingMinutesSinceAsync(Start.AddHours(-24), 1));
      Assert.Equal(30, await db.HeatingMinutesSinceAsync(t0.AddMinutes(30), 1));
    }
    finally { File.Delete(path); }
  }
}
