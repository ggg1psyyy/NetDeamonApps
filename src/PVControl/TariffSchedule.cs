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
    /// <summary>The tariff in force now.</summary>
    public T Active { get; private set; }
    /// <summary>The tariff the user chose (the select's state); equals <see cref="Active"/> when nothing is pending.</summary>
    public T Chosen { get; private set; }
    /// <summary>When <see cref="Chosen"/> takes over; null when nothing is pending.</summary>
    public DateTime? EffectiveFrom { get; private set; }

    /// <summary>A tariff with nothing pending.</summary>
    /// <param name="active">The tariff in force.</param>
    public TariffSchedule(T active) { Active = active; Chosen = active; }

    /// <summary>The tariff in force at a time.</summary>
    /// <param name="t">The time (a quarter's start).</param>
    /// <returns><see cref="Chosen"/> from <see cref="EffectiveFrom"/> on, else <see cref="Active"/>.</returns>
    public T At(DateTime t) => EffectiveFrom is { } from && t >= from ? Chosen : Active;

    /// <summary>The user chose a tariff: it applies from the next switch time; choosing the active one again drops a
    /// pending change.</summary>
    /// <param name="tariff">The chosen tariff.</param>
    /// <param name="now">Current time.</param>
    /// <param name="switchTime">Time of day a change takes effect.</param>
    /// <returns>True if the schedule changed.</returns>
    public bool Choose(T tariff, DateTime now, TimeSpan switchTime)
    {
      if (EqualityComparer<T>.Default.Equals(tariff, Chosen)) return false;
      Chosen = tariff;
      EffectiveFrom = EqualityComparer<T>.Default.Equals(tariff, Active) ? null : NextSwitch(now, switchTime);
      return true;
    }

    /// <summary>The switch time passed: the chosen tariff becomes the active one.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>True if it rolled over.</returns>
    public bool Roll(DateTime now)
    {
      if (EffectiveFrom is not { } from || now < from) return false;
      Active = Chosen;
      EffectiveFrom = null;
      return true;
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
    /// <returns>The schedule, rolled over if its switch time passed while PVControl was down.</returns>
    public static TariffSchedule<T> Restore(T start, string? chosen, string? active, string? effectiveFrom, DateTime now, TimeSpan switchTime)
    {
      T c = Parse(chosen) ?? start;
      var schedule = new TariffSchedule<T>(Parse(active) ?? c);
      if (!EqualityComparer<T>.Default.Equals(c, schedule.Active))
      {
        schedule.Chosen = c;
        // A pending change without its time (lost attribute): from the next switch, as if chosen now.
        schedule.EffectiveFrom = DateTime.TryParse(effectiveFrom, out var from) ? from : NextSwitch(now, switchTime);
        schedule.Roll(now);
      }
      return schedule;
    }

    /// <summary>A tariff name as the select sends it (HA's state is lowercased on the way in).</summary>
    /// <param name="name">The name.</param>
    /// <returns>The tariff; null if unknown.</returns>
    public static T? Parse(string? name) =>
      name is not null && Enum.TryParse(name, ignoreCase: true, out T t) && Enum.IsDefined(t) ? t : null;

    /// <summary>A price list for the schedule: each quarter from the list of the tariff in force then. Each list is
    /// complete for its tariff (including its estimated quarters), so an estimate never mixes two tariffs.</summary>
    /// <param name="listFor">The prices as if a tariff applied everywhere.</param>
    /// <returns><paramref name="listFor"/>(<see cref="Active"/>) when nothing is pending, else both spliced at
    /// <see cref="EffectiveFrom"/>.</returns>
    public PriceList Prices(Func<T, PriceList> listFor) =>
      EffectiveFrom is not { } from ? listFor(Active)
      : new PriceList(listFor(Active).Where(p => p.StartTime < from).Concat(listFor(Chosen).Where(p => p.StartTime >= from)).ToList());
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
