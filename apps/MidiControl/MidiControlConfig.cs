using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.MidiControl;

public class MidiControlConfig
{
    public MqttConfig Mqtt { get; set; } = new();
    public List<EntityMapping> Mappings { get; set; } = new();
}
public class MqttConfig
{
    public string Topic { get; set; } = null!;
    public int RadialNumLeds { get; set; } = 11;
    public List<string>? FaderIDs { get; set; }
    public List<string>? ButtonIDs { get; set; }
    public List<string>? RadialIDs { get; set; }

    public List<int> GetAllFaderIDs()
    {
        return FaderIDs?.TryParseToIntList(out var ints) ?? false ? ints.Order().ToList() : [];
    }
    public List<int> GetAllButtonIDs()
    {
        return ButtonIDs?.TryParseToIntList(out var ints) ?? false ? ints.Order().ToList() : [];
    }
    public List<int> GetAllRadialIDs()
    {
        return RadialIDs?.TryParseToIntList(out var ints) ?? false ? ints.Order().ToList() : [];
    }
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
    
}