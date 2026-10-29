using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.MidiControl;

public class MidiControlConfig
{
    public List<StatusPage> StatusPages { get; set; } = [];
    public int StatusUpdateInterval { get; set; } // in seconds 0=off
    public MqttConfig Mqtt { get; set; } = new();
    public List<EntityMapping> Mappings { get; set; } = new();
}

public class StatusPage
{
    public string Title { get; set; } = string.Empty;
    public List<Entity> StatusEntities { get; set; } = [];
}
public class MqttConfig
{
    public string Topic { get; set; } = null!;
    public int RadialNumLeds { get; set; } = 11;
    public List<string>? FaderIDs { get; set; }
    public List<string>? ButtonIDs { get; set; }
    public List<string>? RadialIDs { get; set; }

    /// <summary>Fader channels (numbers and ranges expanded), sorted.</summary>
    public List<int> GetAllFaderIDs() => ParseIds(FaderIDs);
    /// <summary>Button notes (numbers and ranges expanded), sorted.</summary>
    public List<int> GetAllButtonIDs() => ParseIds(ButtonIDs);
    /// <summary>Radial controllers (numbers and ranges expanded), sorted.</summary>
    public List<int> GetAllRadialIDs() => ParseIds(RadialIDs);

    /// <summary>IDs from config items like "3" or "1-8", sorted.</summary>
    /// <param name="ids">Config items; null = none.</param>
    /// <returns>The IDs; empty if none parse.</returns>
    private static List<int> ParseIds(List<string>? ids) =>
        ids?.TryParseToIntList(out var ints) ?? false ? ints.Order().ToList() : [];
}

public class EntityMapping
{
    public Entity ControlEntity { get; set; } = null!;
    public string EntityType { get; set; } = "";  // cover, light, switch, number, climate, media_player
    
    public MidiSettings MidiControl { get; set; } = new();
    
    public MappingOptions? Options { get; set; }
}

public class MidiSettings
{
    [ConfigurationKeyName("ControlStyle")]
    public string MidiType { get; set; } = "";  // fader, radial, led/button
    [ConfigurationKeyName("FaderID")]
    public int Channel { get; set; } = 1; // fader
    [ConfigurationKeyName("RadialID")]
    public int Controller { get; set; } = 0; // radial
    [ConfigurationKeyName("ButtonID")]
    public int Note { get; set; }  // led
}

public class MappingOptions
{
    public bool Invert { get; set; } = false; // invert the direction of fader
    public string? RangeParameter {get; set;} // which parameter is editable (for example brightness) 
    public MidiSettings? RangeInput { get; set; } // which control changes the parameter
    public MidiSettings? RangeOutput { get; set; } // where is the parameter shown
    public string? ModifierName { get; set; } // use a modifier (button) to access this value
    public string? ModifierValue { get; set; } // which value of the modifier is needed 
    public bool? IsModifier { get; set; } = false; // this (button) is the modifier itself
    public string? SpecialAttribute { get; set; }
}