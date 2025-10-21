using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetDeamon.apps.MidiControl;

[NetDaemonApp]
#if DEBUG
[Focus]
#endif

public class MidiControl : IAsyncInitializable
{
    private readonly MidiControlConfig _config;
    private readonly IHaContext _ha;
    private readonly ILogger<MidiControl> _logger;

    private Dictionary<string, Tuple<string, List<EntityMapping>>> _modifiers;
    //private Tuple<string, string, List<EntityMapping>> _modifiers;

    public MidiControl(IHaContext ha, ILogger<MidiControl> logger, IAppConfig<MidiControlConfig> config)
    {
        _ha = ha;
        _logger = logger;
        _config = config.Value;
        _modifiers = new Dictionary<string, Tuple<string, List<EntityMapping>>>();
        InitializeAllControls();
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MidiControl initilizing");
        try
        {
            // find all modifiers first
            foreach (var mapping in _config.Mappings.Where(m => m.Options is
                         { IsModifier: true, ModifierName: not null, ModifierValue: not null }))
            {
                var modifierName = mapping.Options?.ModifierName ?? string.Empty;
                var setValue = 0;
                if (!_modifiers.ContainsKey(modifierName))
                {
                    _modifiers.Add(modifierName,
                        new Tuple<string, List<EntityMapping>>(
                            mapping.Options?.ModifierValue?.ToLowerInvariant() ?? string.Empty, [mapping]));
                    setValue = 1;
                }
                else
                {
                    _modifiers[modifierName].Item2.Add(mapping);
                }

                var msg = CreateMidiMessage(mapping, setValue);
                if (msg is not null)
                    PublishMidiMessage(msg);
            }
            // subscribe to events
            foreach (var mapping in _config.Mappings)
            {
                mapping.ControlEntity?.StateAllChanges().Throttle(TimeSpan.FromMilliseconds(100))
                    .Subscribe(_ => EntityStateChanged(mapping));
            }
            PublishAllMappings();

            _ha.Events.Where(e => (bool)e.DataElement?.ToString().Contains("event.mqtt_event"))
                .Subscribe(e => HandleMqttMessage((JsonElement)e.DataElement!));
            
            _logger.LogInformation("MidiControl initialized with {Count} mappings", _config.Mappings.Count);
        }
        catch (Exception e)
        {
            _logger.LogError("Error initializing MidiControl: {ErrorMessage}", e.Message);
        }
    }
    /// <summary>
    /// sets all defined faders, buttons/leds, radials to zero/off
    /// </summary>
    private void InitializeAllControls()
    {
        foreach (var id in _config.Mqtt.GetAllFaderIDs())
        {
            var msg = new MidiMessage
            {
                channel = id,
                note = 0,
                controller = 0,
                value = 0,
                valueraw = 0,
                event_type = "fader"
            };
            PublishMidiMessage(msg);
            System.Threading.Thread.Sleep(10);
        }
        foreach (var id in _config.Mqtt.GetAllButtonIDs())
        {
            var msg = new MidiMessage
            {
                channel = 0,
                note = id,
                controller = 0,
                value = 0,
                valueraw = 0,
                event_type = "led"
            };
            PublishMidiMessage(msg);
            System.Threading.Thread.Sleep(10);
        }
        foreach (var id in _config.Mqtt.GetAllRadialIDs())
        {
            var msg = new MidiMessage
            {
                channel = 0,
                note = 0,
                controller = id,
                value = 0,
                valueraw = 0,
                event_type = "radial"
            };
            PublishMidiMessage(msg);
            System.Threading.Thread.Sleep(10);
        }
    }
    private void PublishAllMappings()
    {
        foreach (var mapping in _config.Mappings)
        {
            var msg = CreateMidiMessage(mapping);
            if (msg is not null)
                PublishMidiMessage(msg);
        }
    }

    private void SwitchModifier(string modifierName, string modifierValue)
    {
        if (!_modifiers.ContainsKey(modifierName))
        {
            _logger.LogError("Unknown modifier name: {ModifierName}", modifierName);
            return;
        }

        _modifiers[modifierName] =
            new Tuple<string, List<EntityMapping>>(modifierValue, _modifiers[modifierName].Item2);
        PublishAllMappings();
    }

    private void HandleMqttMessage(JsonElement msgElement)
    {
        //_logger.LogDebug("received event: {event}", msgElement);
        try
        {
            var payload = msgElement.GetProperty("new_state").GetProperty("attributes");
            var midiMsg = JsonSerializer.Deserialize<MidiMessage>(payload.GetRawText());
            if (midiMsg is null)
            {
                _logger.LogError("Received null midi message");
                return;
            }

            var mapping = FindMappingFromMidiMessage(midiMsg);
            if (mapping is null)
            {
                _logger.LogWarning("Couldn't find mapping for: {midiMsg}", midiMsg.ToString());
                return;
            }

            SetEntityValue(mapping, midiMsg);
            PublishAllMappings();
        }
        catch (Exception e)
        {
            _logger.LogError("Error handling midi message: {ErrorMessage}", e.Message);
        }
    }

    private void SetEntityValue(EntityMapping mapping, MidiMessage msg)
    {
        var value = msg.value ?? 0;
        if (mapping.ControlEntity is null)
        {
            // only execute once (button presses are sent on press and release!)
            if (value == 0)
                return;
            var isModifier = mapping.Options?.IsModifier == true;
            var modifierName = mapping.Options?.ModifierName?.ToLowerInvariant() ?? string.Empty;
            var modifierValue = mapping.Options?.ModifierValue?.ToLowerInvariant() ?? string.Empty;
            if (!isModifier)
            {
                _logger.LogWarning("Entity is null, but mapping is not a modifier. {midi}",
                    mapping.MidiControl.ToString());
                return;
            }

            SwitchModifier(modifierName, modifierValue);
        }

        switch (mapping.ControlEntity.GetEntityPlatform().ToLowerInvariant())
        {
            case "cover":
                mapping.ControlEntity.SetCoverPosition(value);
                break;
            case "light":
                if (msg.event_type.Equals("noteon", StringComparison.InvariantCultureIgnoreCase) && value != 0)
                    mapping.ControlEntity.ToggleLight();
                if (msg.event_type.Equals("controlchange", StringComparison.InvariantCultureIgnoreCase) && value != 0)
                    if (mapping.ControlEntity.TryGetStateValue(out bool lightOn))
                    {
                        // don't change anything when light is off
                        if (!lightOn)
                            break;
                        switch (mapping?.Options?.RangeParameter?.ToLowerInvariant())
                        {
                            case "brightness":
                                if (!mapping.ControlEntity.TryGetBrightness(out var currentBrigthness))
                                    break;
                                currentBrigthness += value;
                                currentBrigthness = Math.Clamp(currentBrigthness, 0, 255);
                                mapping.ControlEntity.SetBrightness(currentBrigthness);
                                break;
                            default:
                                _logger.LogWarning("RangeParameter {param} not implemented",
                                    mapping?.Options?.RangeParameter);
                                break;
                        }
                    }

                break;
        }
    }

    private EntityMapping? FindMappingFromMidiMessage(MidiMessage msg)
    {
        List<EntityMapping> entitiesFound;
        switch (msg.event_type.ToLowerInvariant())
        {
            case "noteon":
            case "noteoff":
            case "led":
                entitiesFound = _config.Mappings.Where(m => m.MidiControl.MidiType.Equals("led", StringComparison.InvariantCultureIgnoreCase) && m.MidiControl.Note == msg.note).ToList();
                break;
            case "pitchbend":
            case "fader":
                entitiesFound = _config.Mappings.Where(m => m.MidiControl.MidiType.Equals("fader", StringComparison.InvariantCultureIgnoreCase) && m.MidiControl.Channel == msg.channel).ToList();
                break;
            case "controlchange":
            case "radial":
                entitiesFound = _config.Mappings.Where(m => (m.MidiControl.MidiType.Equals("radial", StringComparison.InvariantCultureIgnoreCase) && m.MidiControl.Controller == msg.controller) 
                                                            || ((m.Options?.RangeInput?.MidiType.Equals("radial", StringComparison.InvariantCultureIgnoreCase) ?? false) && m.Options?.RangeInput?.Controller == msg.controller)).ToList();
                break;
            default:
                entitiesFound = [];
                break;
        }

        if (entitiesFound is null || !entitiesFound.Any())
            return null;
        if (entitiesFound.Count == 1)
            return entitiesFound.First();
        // more than one -> look for modifiers
        var modifiers = entitiesFound.GroupBy(m => m.Options?.ModifierName?.ToLowerInvariant() ?? string.Empty).ToList();
        if (modifiers.Count == 1)
        {
            var modifierValue = _modifiers[modifiers.First().Key].Item1?.ToLowerInvariant() ?? string.Empty;
            return entitiesFound.FirstOrDefault(m => m.Options?.ModifierValue?.ToLowerInvariant() == modifierValue);
        }

        _logger.LogError("Found multiple modifiers for mappings with same midi");
        return null;
    }

    private void EntityStateChanged(EntityMapping mapping)
    {
        var msg = CreateMidiMessage(mapping);
        if (msg is not null)
            PublishMidiMessage(msg);
    }

    private MidiMessage? CreateMidiMessage(EntityMapping mapping, int overrideValue = int.MinValue)
    {
        var modifierName = mapping.Options?.ModifierName?.ToLowerInvariant() ?? string.Empty;
        var modifierValue = mapping.Options?.ModifierValue?.ToLowerInvariant() ?? string.Empty;
        if (mapping.ControlEntity is null)
        {
            if (overrideValue != int.MinValue)
                return new MidiMessage
                {
                    event_type = "led",
                    channel = 0,
                    note = mapping.MidiControl.Note,
                    value = overrideValue
                };

            return new MidiMessage
            {
                event_type = "led",
                channel = 0,
                note = mapping.MidiControl.Note,
                value = _modifiers.TryGetValue(modifierName, out var value1) && value1.Item1 == modifierValue ? 1 : 0
            };
        }

        // don't output if mapping has modifier and other modifier is currently selected
        if (_modifiers.TryGetValue(modifierName, out var value) && value.Item1 != modifierValue)
            return null;

        switch (mapping.ControlEntity.GetEntityPlatform().ToLowerInvariant())
        {
            case "cover":
                if (!mapping.ControlEntity.TryGetCoverPosition(out int position))
                    return null;

                return new MidiMessage
                {
                    event_type = "fader",
                    channel = mapping?.MidiControl?.Channel,
                    value = mapping?.Options?.Invert == true ? 100 - position : position
                };
            case "light":
                if (!mapping.ControlEntity.TryGetStateValue(out bool on))
                    return null;
                // simple light with button - only on/off
                if (mapping.Options?.RangeParameter is null)
                    return new MidiMessage
                    {
                        event_type = "led",
                        channel = 0,
                        note = mapping.MidiControl.Note,
                        value = on ? 1 : 0
                    };

                if (mapping.Options?.RangeParameter?.ToLowerInvariant() == "brightness")
                {
                    var currentBrightness = 0;
                    if (on && !mapping.ControlEntity.TryGetBrightness(out currentBrightness))
                        return null;
                    // brightness is mapped to radial leds
                    return new MidiMessage
                    {
                        event_type = "radial",
                        channel = 0,
                        controller = mapping.Options?.RangeOutput?.Controller,
                        value = on ? ConvertRange(currentBrightness, newMax: _config.Mqtt.RadialNumLeds) + 200 : 0
                    };
                }

                break;
        }

        return null;
    }

    private void PublishMidiMessage(MidiMessage msg)
    {
        var json = JsonSerializer.Serialize(msg);

        _ha.CallService("mqtt", "publish", data: new
        {
            topic = _config.Mqtt.Topic,
            payload = json
        });
        _logger.LogTrace("Published MIDI: {Json}", json);
    }

    private static int ConvertRange(int value, int curMax = 255, int newMax = 11)
    {
        var scaledValue = (double)value / curMax * newMax;
        return (int)Math.Round(scaledValue);
    }
}

public class MidiMessage
{
    public required string event_type { get; init; }
    public int? channel { get; init; }
    public long timestamp { get; init; }
    public int? value { get; init; }
    public int? valueraw { get; init; }
    public int? note { get; init; }
    public int? controller { get; init; }

    public override string ToString()
    {
        return event_type + " - Channel:" + channel + " Note:" + note + " Controller:" + controller + " Value:" + value;
    }
}

public enum MappingType
{
    Cover, // 0-100 position
    Light, // on/off with brightness
    Switch, // on/off
    Number, // numeric input
    Climate, // temperature control
    MediaPlayer // volume control
}

public enum MidiControlType
{
    Fader, // PitchBend (0-100)
    Radial, // ControlChange (rotary encoder)
    Led // NoteOn (button LED)
}