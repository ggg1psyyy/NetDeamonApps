using System.Collections.Generic;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.HeatingTest;

/// <summary>YAML configuration of the heating step test (HeatingTest.yaml).</summary>
public class HeatingTestConfig
{
  /// <summary>false: the app does nothing at all — no entities, no timer, no log.</summary>
  public bool Enabled { get; set; }
  /// <summary>The SQLite log; relative = in the plugin's folder (<see cref="AppFiles.PathOf"/>).</summary>
  public string DBLocation { get; set; } = "heating_test.db";
  /// <summary>Seconds between two log samples.</summary>
  public int SampleSeconds { get; set; } = 60;

  /// <summary>Heating-curve level (number) — the only thing the test changes.</summary>
  public Entity LevelEntity { get; set; } = null!;
  public Entity? SlopeEntity { get; set; }
  /// <summary>Outdoor temperature used for the start condition (the heat pump's own sensor).</summary>
  public Entity OutdoorEntity { get; set; } = null!;
  public Entity? OutdoorHouseEntity { get; set; }
  public Entity? FlowEntity { get; set; }
  public Entity? FlowSetpointEntity { get; set; }
  public Entity? ReturnEntity { get; set; }
  public Entity CompressorEntity { get; set; } = null!;
  public Entity? PowerEntity { get; set; }
  public Entity? CopEntity { get; set; }
  /// <summary>On while the heat pump heats the house (not warm water).</summary>
  public Entity HeatingActiveEntity { get; set; } = null!;
  public Entity? WarmWaterActiveEntity { get; set; }

  /// <summary>The heat pump's operating mode (select); a night only runs in one of <see cref="HeatingModes"/>. Optional.</summary>
  public Entity? HeatPumpModeEntity { get; set; }
  /// <summary>Modes of <see cref="HeatPumpModeEntity"/> that include space heating.</summary>
  public List<string> HeatingModes { get; set; } = [];

  public List<RoomConfig> Rooms { get; set; } = [];

  /// <summary>A test night only starts when it's at most this warm outside at the start time.</summary>
  public double MaxOutdoorAtStart { get; set; } = 6;
  /// <summary>...and the heat pump heated the house at least this long in the last 24 h.</summary>
  public int MinHeatingMinutes24h { get; set; } = 120;

  public string DefaultStartTime { get; set; } = "22:00";
  public double DefaultDurationHours { get; set; } = 12;
  public double DefaultOffsetK { get; set; } = -3;
}

public class RoomConfig
{
  public string Name { get; set; } = "";
  /// <summary>The room's climate entity (current_temperature, temperature = setpoint).</summary>
  public Entity Climate { get; set; } = null!;
  /// <summary>A test aborts when the room is more than this below its setpoint; omit to only log the room.</summary>
  public double? AbortBelowK { get; set; }
}
