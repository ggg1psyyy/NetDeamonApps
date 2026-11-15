using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl
{
  /// <summary>The import contract's energy price: the market price (× multiplier + addition) or a fixed price.</summary>
  public enum ImportTariff { Market, Fixed }

  /// <summary>The feed-in price: the import market price (scaled with the export settings), a fixed price, or its own
  /// market price entities (<see cref="PVConfig.CurrentExportPriceEntities"/>).</summary>
  public enum ExportTariff { SameAsImport, Fixed, Market }

  /// <summary>
  /// A tariff that the user may change, effective from the next switch time (<see cref="PVConfig.TariffSwitchTime"/>,
  /// e.g. 00:00 — the supplier changes the contract from the next day). Until then the plan prices each quarter with
  /// the tariff in force then: the quarters before the switch with <see cref="Active"/>, those from it with
  /// <see cref="Chosen"/> — tonight's plan already prices tomorrow right.
  /// </summary>
  /// <typeparam name="T">The tariff enum.</typeparam>
  public sealed class TariffSchedule<T> where T : struct, Enum
  {
    /// <summary>The schedule's state, one immutable value: the select (another thread) changes it by swapping in a new one,
    /// so a reader that takes it once never sees a choice half written (prices and fixed quarters of one cycle from one state).</summary>
    private sealed record State(T Active, T Chosen, DateTime? EffectiveFrom, T? Previous, DateTime? Since);
    private volatile State _s;
    private readonly object _write = new();

    /// <summary>The tariff in force now.</summary>
    public T Active => _s.Active;
    /// <summary>The tariff the user chose (the select's state); equals <see cref="Active"/> when nothing is pending.</summary>
    public T Chosen => _s.Chosen;
    /// <summary>When <see cref="Chosen"/> takes over; null when nothing is pending.</summary>
    public DateTime? EffectiveFrom => _s.EffectiveFrom;
    /// <summary>The tariff before the last switch: quarters before <see cref="Since"/> keep it (the cost log books a
    /// quarter just after it ends — after the roll).</summary>
    public T? Previous => _s.Previous;
    /// <summary>When <see cref="Active"/> took over; null if it never switched.</summary>
    public DateTime? Since => _s.Since;

    /// <summary>A tariff with nothing pending.</summary>
    /// <param name="active">The tariff in force.</param>
    public TariffSchedule(T active) => _s = new(active, active, null, null, null);

    private TariffSchedule(State s) => _s = s;

    /// <summary>The schedule as it is now, unaffected by later choices (several reads that must belong together).</summary>
    public TariffSchedule<T> Copy() => new(_s);

    /// <summary>The tariff in force at a time.</summary>
    /// <param name="t">The time (a quarter's start).</param>
    /// <returns><see cref="Chosen"/> from <see cref="EffectiveFrom"/> on, <see cref="Previous"/> before <see cref="Since"/>,
    /// else <see cref="Active"/>.</returns>
    public T At(DateTime t) => At(_s, t);

    private static T At(State s, DateTime t) =>
      s.EffectiveFrom is { } from && t >= from ? s.Chosen
      : s.Since is { } since && t < since && s.Previous is { } previous ? previous
      : s.Active;

    /// <summary>The user chose a tariff: it applies from the next switch time; choosing the active one again drops a
    /// pending change.</summary>
    /// <param name="tariff">The chosen tariff.</param>
    /// <param name="now">Current time.</param>
    /// <param name="switchTime">Time of day a change takes effect.</param>
    /// <returns>True if the schedule changed.</returns>
    public bool Choose(T tariff, DateTime now, TimeSpan switchTime)
    {
      lock (_write)
      {
        var s = _s;
        if (EqualityComparer<T>.Default.Equals(tariff, s.Chosen)) return false;
        _s = s with { Chosen = tariff, EffectiveFrom = EqualityComparer<T>.Default.Equals(tariff, s.Active) ? null : NextSwitch(now, switchTime) };
        return true;
      }
    }

    /// <summary>The switch time passed: the chosen tariff becomes the active one.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>True if it rolled over.</returns>
    public bool Roll(DateTime now)
    {
      lock (_write)
      {
        var s = _s;
        if (s.EffectiveFrom is not { } from || now < from) return false;
        _s = new State(s.Chosen, s.Chosen, null, s.Active, from);
        return true;
      }
    }

    /// <summary>The next switch time after a time.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="switchTime">Time of day.</param>
    /// <returns>Today's switch time if still ahead, else tomorrow's.</returns>
    public static DateTime NextSwitch(DateTime now, TimeSpan switchTime)
    {
      var today = now.Date + switchTime;
      return today > now ? today : today.AddDays(1);
    }

    /// <summary>The schedule after a restart, from the select (chosen) and its attributes (active, effective_from).</summary>
    /// <param name="start">The YAML's tariff: the start value when the entity has nothing yet.</param>
    /// <param name="chosen">The select's state.</param>
    /// <param name="active">Attribute "active".</param>
    /// <param name="effectiveFrom">Attribute "effective_from" (ISO 8601).</param>
    /// <param name="now">Current time.</param>
    /// <param name="switchTime">Time of day a change takes effect.</param>
    /// <param name="previous">Attribute "previous" (the tariff before the last switch).</param>
    /// <param name="since">Attribute "since" (ISO 8601: the last switch).</param>
    /// <returns>The schedule, rolled over if its switch time passed while PVControl was down.</returns>
    public static TariffSchedule<T> Restore(T start, string? chosen, string? active, string? effectiveFrom, DateTime now, TimeSpan switchTime,
      string? previous = null, string? since = null)
    {
      T c = Parse(chosen) ?? start;
      T a = Parse(active) ?? c;
      var state = new State(a, a, null, null, null);
      if (Parse(previous) is { } p && DateTime.TryParse(since, out var s))
        state = state with { Previous = p, Since = s };
      if (!EqualityComparer<T>.Default.Equals(c, a))
        // A pending change without its time (lost attribute): from the next switch, as if chosen now.
        state = state with { Chosen = c, EffectiveFrom = DateTime.TryParse(effectiveFrom, out var from) ? from : NextSwitch(now, switchTime) };
      var schedule = new TariffSchedule<T>(state);
      schedule.Roll(now);
      return schedule;
    }

    /// <summary>A tariff name as the select sends it (HA's state is lowercased on the way in).</summary>
    /// <param name="name">The name.</param>
    /// <returns>The tariff; null if unknown.</returns>
    public static T? Parse(string? name) =>
      name is not null && Enum.TryParse(name, ignoreCase: true, out T t) && Enum.IsDefined(t) ? t : null;

    /// <summary>Where a tariff is in force between two times, as periods of whole quarters (<see cref="At"/> per quarter).</summary>
    /// <param name="tariff">The tariff.</param>
    /// <param name="from">From here (its quarter).</param>
    /// <param name="until">Until here (exclusive).</param>
    /// <returns>The periods [start, end), in order.</returns>
    public List<(DateTime Start, DateTime End)> PeriodsOf(T tariff, DateTime from, DateTime until)
    {
      var s = _s;   // one state for every quarter
      var periods = new List<(DateTime, DateTime)>();
      DateTime? open = null;
      var t = TimeRange.Floor(from, TimeRange.SlotMinutes);
      for (; t < until; t = t.AddMinutes(TimeRange.SlotMinutes))
      {
        bool inForce = EqualityComparer<T>.Default.Equals(At(s, t), tariff);
        if (inForce && open is null) open = t;
        else if (!inForce && open is { } o) { periods.Add((o, t)); open = null; }
      }
      if (open is { } last) periods.Add((last, t));
      return periods;
    }

    /// <summary>A price list for the schedule: each quarter from the list of the tariff in force then. Each list is
    /// complete for its tariff (including its estimated quarters), so an estimate never mixes two tariffs.</summary>
    /// <param name="listFor">The prices as if a tariff applied everywhere.</param>
    /// <returns>Each quarter from the list of <see cref="At"/>(its start); <paramref name="listFor"/>(<see cref="Active"/>)
    /// itself when no quarter differs.</returns>
    public PriceList Prices(Func<T, PriceList> listFor)
    {
      var s = _s;   // one state for every quarter
      var active = listFor(s.Active);
      bool pending = s.EffectiveFrom is not null, past = s.Since is not null && s.Previous is not null;
      if (!pending && !past) return active;
      var lists = new Dictionary<T, PriceList> { [s.Active] = active };
      PriceList For(T t) => lists.TryGetValue(t, out var l) ? l : lists[t] = listFor(t);
      // Each tariff's list may cover other quarters (e.g. a fixed price only where the market has slots): take every
      // quarter from the list of its own tariff.
      var all = new[] { s.Active, s.Chosen }.Concat(s.Previous is { } pr ? [pr] : Array.Empty<T>()).Distinct()
        .SelectMany(t => For(t).Where(p => EqualityComparer<T>.Default.Equals(At(s, p.StartTime), t)));
      return new PriceList(all.OrderBy(p => p.StartTime).ToList());
    }
  }
  /// <summary>Whether a tariff can be priced with the configured sources (a choice in HA must not turn a missing
  /// setting into a price of 0).</summary>
  public static class TariffSources
  {
    /// <summary>What the import tariff lacks.</summary>
    /// <param name="tariff">The tariff.</param>
    /// <param name="hasFixedEntity">ImportPriceFixedEntity is set.</param>
    /// <param name="fixedValue">ImportPriceFixedValue.</param>
    /// <returns>The missing settings; null if it can be priced.</returns>
    public static string? ImportMissing(ImportTariff tariff, bool hasFixedEntity, float fixedValue) =>
      tariff == ImportTariff.Fixed && !hasFixedEntity && fixedValue <= 0 ? "ImportPriceFixedEntity or ImportPriceFixedValue" : null;

    /// <summary>What the export tariff lacks.</summary>
    /// <param name="tariff">The tariff.</param>
    /// <param name="hasFixedEntity">ExportPriceFixedEntity is set.</param>
    /// <param name="fixedValue">ExportPriceFixedValue.</param>
    /// <param name="marketEntities">Number of CurrentExportPriceEntities.</param>
    /// <returns>The missing settings; null if it can be priced.</returns>
    public static string? ExportMissing(ExportTariff tariff, bool hasFixedEntity, float fixedValue, int marketEntities) => tariff switch
    {
      ExportTariff.Fixed when !hasFixedEntity && fixedValue <= 0 => "ExportPriceFixedEntity or ExportPriceFixedValue",
      ExportTariff.Market when marketEntities == 0 => "CurrentExportPriceEntities",
      _ => null,
    };
  }
}
