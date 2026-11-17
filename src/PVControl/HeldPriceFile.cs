using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NetDeamon.apps.PVControl
{
  /// <summary>
  /// The price lists PVControl holds (<see cref="PriceList.MergeHeld"/>), kept in a file next to the plugin so a restart
  /// while every price source is down still plans with the day's real prices. Netto, quarter-hourly, the entities'
  /// format (<see cref="PriceTableEntry"/>). Kept by the deploy (deploy.py's KEEP).
  /// </summary>
  public static class HeldPriceFile
  {
    /// <summary>The file's name in the plugin's folder.</summary>
    public const string FileName = "held_prices.json";

    private sealed record Content(List<PriceTableEntry> Import, List<PriceTableEntry> Export);

    /// <summary>Writes the lists (replacing the file in one step: a crash never leaves half a file).</summary>
    /// <param name="path">The file.</param>
    /// <param name="import">Import netto prices.</param>
    /// <param name="export">The export's own netto prices (empty unless the export tariff has its own market).</param>
    public static void Save(string path, PriceList import, PriceList export)
    {
      string tmp = path + ".tmp";
      File.WriteAllText(tmp, JsonSerializer.Serialize(new Content(import.ToList(), export.ToList())));
      File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads the lists.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Both lists; empty ones if the file is missing or unreadable (nothing held is the safe answer).</returns>
    public static (PriceList Import, PriceList Export) Load(string path)
    {
      try
      {
        if (File.Exists(path) && JsonSerializer.Deserialize<Content>(File.ReadAllText(path)) is { } c)
          return (new PriceList(c.Import ?? []), new PriceList(c.Export ?? []));
      }
      catch (JsonException) { }
      catch (IOException) { }
      return (new PriceList(), new PriceList());
    }
  }
}
