using LinqToDB;
using PVControl;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Predictions
{
  /// <summary>Base load forecast: the age-weighted average of each hour of day from the energy history DB, split into quarters.</summary>
  public class HourlyWeightedAverageLoadPrediction : Prediction
  {
    private readonly string _dbLocation;
    private readonly float _weightScaling;
    private readonly bool _baseLoadOnly;
    private readonly IReadOnlyList<string> _excludeColumns;

    /// <summary>Creates and fills the load forecast from the energy history DB.</summary>
    /// <param name="dbLocation">Path of the SQLite energy history DB.</param>
    /// <param name="weightScaling">Age weighting: a day weighs exp(−age in days / weightScaling).</param>
    /// <param name="baseLoadOnly">Subtract EV charging and warm water from the house load.</param>
    /// <param name="excludeColumns">
    /// DB columns subtracted from the house load (loads added back as ExtraLoads):
    /// "carcharge", "warmwaterenergy", "heatpumpenergy". Ignored with <paramref name="baseLoadOnly"/>.
    /// </param>
    public HourlyWeightedAverageLoadPrediction(string dbLocation, float weightScaling = 20f, bool baseLoadOnly = false, IReadOnlyList<string>? excludeColumns = null)
    {
      _dbLocation = dbLocation;
      if (string.IsNullOrEmpty(_dbLocation) || !System.IO.File.Exists(_dbLocation))
        throw new ArgumentException("DBLocation missing or file not found");

      _weightScaling = weightScaling;
      _baseLoadOnly = baseLoadOnly;
      _excludeColumns = excludeColumns ?? [];
      Initialize("Load Prediction");
    }

    /// <summary>Today and tomorrow get the same hourly averages, a quarter of each per slot.</summary>
    /// <param name="now">Day of the window.</param>
    /// <returns>Wh by slot start.</returns>
    protected override Dictionary<DateTime, int> PopulateData(DateTime now)
    {
      DateTime today = now.Date;
      var hourly = HourlyWeightedAverages(LoadHistory(), today, _weightScaling);

      Dictionary<DateTime, int> data = [];
      for (var time = today; time < today.AddDays(2); time = time.AddMinutes(15))
        data.Add(time, Energy.SlotWh(hourly[time.Hour])); // hourly Wh = mean W
      return data;
    }

    /// <summary>All logged hours with house energy, as (timestamp, value after exclusions).</summary>
    private List<(DateTime Timestamp, int Value)> LoadHistory()
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(string.Format("Data Source={0}", _dbLocation)));
      // Subtraction is done in memory so the exclusion list can be dynamic. The hour is NOT
      // filtered in SQL: LinqToDB turns Timestamp.Hour into strftime('%H', …), which SQLite
      // evaluates in UTC — that shifted every forecast hour by the UTC offset (1–2 h).
      return db.Hourlies
        .Where(h => h.Houseenergy != null)
        .Select(h => new { h.Timestamp, h.Houseenergy, h.Carcharge, h.Warmwaterenergy, h.Heatpumpenergy })
        .AsEnumerable()
        .Select(r => (r.Timestamp, ComputeValue(r.Houseenergy, r.Carcharge, r.Warmwaterenergy, r.Heatpumpenergy)))
        .ToList();
    }

    /// <summary>Weighted average energy per local hour of day; older days weigh less: exp(−age in days / weightScaling).</summary>
    /// <param name="rows">Hourly rows, Wh (timestamp hh:59:59 covers hour hh; UTC is converted to local).</param>
    /// <param name="today">Reference day for the age.</param>
    /// <param name="weightScaling">Age weighting in days.</param>
    /// <returns>Wh per hour 0–23; 0 for hours without history.</returns>
    public static int[] HourlyWeightedAverages(IEnumerable<(DateTime Timestamp, int Value)> rows, DateTime today, float weightScaling)
      => Mean.HourOfDayProfile(rows.Select(r => (r.Timestamp.AsLocal(), (double)r.Value)),
          today, weightScaling).Select(v => (int)Math.Round(v)).ToArray();

    /// <summary>House energy of an hour minus the excluded loads.</summary>
    /// <param name="houseenergy">House energy, Wh.</param>
    /// <param name="carcharge">EV charging, Wh.</param>
    /// <param name="warmwaterenergy">Warm water, Wh.</param>
    /// <param name="heatpumpenergy">Heat pump, Wh.</param>
    /// <returns>Wh.</returns>
    private int ComputeValue(int? houseenergy, int? carcharge, int? warmwaterenergy, int? heatpumpenergy)
    {
      int value = houseenergy ?? 0;
      if (_baseLoadOnly)
        return value - (carcharge ?? 0) - (warmwaterenergy ?? 0);
      return value - (int)ExcludedEnergy(_excludeColumns, carcharge, warmwaterenergy, heatpumpenergy);
    }

    /// <summary>Energy of the excluded DB columns in an hour (loads the simulation adds back as ExtraLoads).</summary>
    /// <param name="columns">Column names: "carcharge", "warmwaterenergy", "heatpumpenergy" (others count 0).</param>
    /// <param name="carcharge">EV charging, Wh.</param>
    /// <param name="warmwaterenergy">Warm water, Wh.</param>
    /// <param name="heatpumpenergy">Heat pump, Wh.</param>
    /// <returns>Wh.</returns>
    public static double ExcludedEnergy(IEnumerable<string> columns, double? carcharge, double? warmwaterenergy, double? heatpumpenergy) =>
      columns.Sum(c => c.ToLowerInvariant() switch
      {
        "carcharge"       => carcharge ?? 0,
        "warmwaterenergy" => warmwaterenergy ?? 0,
        "heatpumpenergy"  => heatpumpenergy ?? 0,
        _                 => 0,
      });
  }
}
