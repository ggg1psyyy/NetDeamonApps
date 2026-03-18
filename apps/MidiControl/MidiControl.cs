using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NetDeamon.apps.MidiControl;

[NetDaemonApp]
// #if DEBUG
// [Focus]
// #endif

public class MidiControl : IAsyncInitializable
{
    private readonly MidiControlConfig _config;
    private readonly IHaContext _ha;
    private readonly ILogger<MidiControl> _logger;
    private static DisposableScheduler _scheduler; 
    private DateTime _lastMessageSent = DateTime.UtcNow;
    private int _currentStatusPage = 1;

    private Dictionary<string, Tuple<string, List<EntityMapping>>> _modifiers;
    //private Tuple<string, string, List<EntityMapping>> _modifiers;

    public MidiControl(IHaContext ha, ILogger<MidiControl> logger, IAppConfig<MidiControlConfig> config, IScheduler  scheduler)
    {
        _ha = ha;
        _logger = logger;
        _config = config.Value;
        _modifiers = new Dictionary<string, Tuple<string, List<EntityMapping>>>();
        _scheduler = (DisposableScheduler)scheduler;
        _ = SendCurrentStatus(force:true);
#if !DEBUG
        InitializeAllControls();
#endif
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
                var modifierName = mapping.Options?.ModifierName?.ToLowerInvariant() ?? string.Empty;
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

                var msg = await CreateMidiMessage(mapping, setValue);
                if (msg is not null)
                    PublishMidiMessage(msg);
            }
            // subscribe to events
            foreach (var mapping in _config.Mappings)
            {
                mapping.ControlEntity?.StateAllChanges().Throttle(TimeSpan.FromMilliseconds(100))
                    .Subscribe(_ => EntityStateChanged(mapping));
            }
            PublishAllMappings(sendInfo: false);

            _ha.Events.Where(e => (bool)e.DataElement?.ToString().Contains("event.mqtt_event"))
                .Subscribe(e => HandleMqttMessage((JsonElement)e.DataElement!));
#if !DEBUG
            if (_config.StatusUpdateInterval > 0 && _config.StatusPages.Count > 0)
            {
                string cron = $"*/{_config.StatusUpdateInterval} * * * * *";
                _scheduler.ScheduleCron(cron, () => SendCurrentStatus(), true);
            }
#endif
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

    private async Task<Image<Rgba32>?> CreateStatusImage(int width=240, int height=320, int numCols = 2, int numRows = 4)
    {
        var image = new Image<Rgba32>(240, 320);
        int curRow = 0;
        int curCol = 0;
        int colWidth = width / numCols;
        int rowHeight = height / numRows;
        foreach (var statusEntity in _config.StatusPages[_currentStatusPage].StatusEntities)
        {
            var entityImage = await CreateEntityImage(statusEntity, colWidth, rowHeight);
            int x = colWidth * curCol;
            int y = rowHeight * curRow;
            if (entityImage is not null)
                image.Mutate(ctx => { ctx.DrawImage(entityImage, new Point(x, y), 1f); });
            
            curCol = curCol < numCols - 1 ? curCol + 1 : 0;
            curRow = curCol > 0 ? curRow : curRow + 1;
            if (curRow > numRows)
                break;
        }

        return image;
    }
    private async Task<Image<Rgba32>?> CreateEntityImage(Entity entity,int width =240, int height=320)
    {
        if (entity is null)
            return null;
        bool displayGraph = false;
        switch (entity.GetEntityPlatform().ToLowerInvariant())
        {
            case "cover":
                displayGraph = false;
                break;
            case "light":
                displayGraph = false;
                break;
        }

        var iconColor = Color.ParseHex("#64B5F6");
        if (entity.TryGetStateValue(out bool onoff))
        {
            if (onoff)
                iconColor = Color.ParseHex("#FFA72630");
        }
        var imgConfig = new ImageConfig
        {
            Width = width,
            Height = height,
            BackgroundColor = Color.ParseHex("#2B2B2B"),
            GraphLineColor = Color.ParseHex("#FFA726"),
            GraphFillColor = Color.ParseHex("#FFA72630"),
            TextColor  = Color.LightGrey,
            IconColor  = iconColor,
            ShowGraph = displayGraph,
            CornerRadius = (int)(width / 20)
        };

        var generator = new ImageGenerator(imgConfig);

        //var graphData = new List<float> { 19.5f, 19.8f, 20.0f, 20.2f, 20.5f, 20.3f, 20.1f, 20.1f };
        var imageData = generator.CreateEntityImage(
            name: entity.TryGetAttribute("friendly_name", out var name) ? name : "unknown entity",
            iconName: entity.TryGetIconString(out string iconName) ? iconName : "mdi:crosshairs-question",
            value: entity.TryGetStateValue(out string value) ? value : "0",
            unit: entity.TryGetAttribute("unit_of_measurement", out var unit) ? unit : ""
            //graphData: graphData
        );
        return await imageData;
    }
    private async Task PublishAllMappings(bool sendInfo = true)
    {
        foreach (var mapping in _config.Mappings)
        {
            var msg = await CreateMidiMessage(mapping, sendInfo: sendInfo);
            if (msg is not null)
                PublishMidiMessage(msg);
        }
    }
    private async Task SendCurrentStatus(bool force = false)
    {
        if ((DateTime.UtcNow - _lastMessageSent).TotalSeconds < _config.StatusUpdateInterval && !force)
            return;
        int maxLine = 19;
        string note = "";
        foreach (var statusEntity in _config.StatusPages[_currentStatusPage].StatusEntities)
        {
            if (!statusEntity.TryGetStateValue(out string value))
                continue;
            statusEntity.TryGetAttribute("friendly_name", out string? name);
            statusEntity.TryGetAttribute("unit_of_measurement", out string? unit);
            name = name?.Truncate(maxLine - value.Length - unit?.Length ?? 0 - 2);
            if (!string.IsNullOrEmpty(name))
                note += name + ": ";
            note += value;
            note += unit;
            note += '\n';
        }

        var img = await CreateStatusImage();
        
        if (string.IsNullOrWhiteSpace(note) && img is null)
            return;
        using var memoryStream = new MemoryStream();
        img.SaveAsPng(memoryStream);
#if DEBUG
        File.WriteAllBytes(@"D:\TEMP\NetDeamon\status.png", memoryStream.ToArray());
#endif
        byte[] bytes = memoryStream.ToArray();
        var imageString = Convert.ToBase64String(bytes);
        
        var msg = new MidiMessage
        {
            event_type = "display",
            info = note,
            image = imageString,
        };
        PublishMidiMessage(msg);
    }
    private void SwitchModifier(string modifierName, string modifierValue)
    {
        if (!_modifiers.ContainsKey(modifierName))
        {
            _logger.LogError("Unknown modifier name: {ModifierName}", modifierName);
            return;
        }

        if (modifierName.Equals("StatusPage", StringComparison.InvariantCultureIgnoreCase))
        {
            int satusPageCount = _config.StatusPages.Count;
            if (modifierValue.Equals("prev", StringComparison.InvariantCultureIgnoreCase))
            {
                _currentStatusPage = _currentStatusPage > 0  ? _currentStatusPage - 1 : satusPageCount - 1;
                
            }
            else if (modifierValue.Equals("next", StringComparison.InvariantCultureIgnoreCase))
            {
                _currentStatusPage = _currentStatusPage < satusPageCount - 1 ? _currentStatusPage + 1 : 0;
            }
            _ = SendCurrentStatus(force:true);
            return;
        }
        _modifiers[modifierName] =
            new Tuple<string, List<EntityMapping>>(modifierValue, _modifiers[modifierName].Item2);
        PublishAllMappings(sendInfo: false);
        var msg = new MidiMessage
        {
            event_type = "display",
            info = modifierName + "\nswitched to:\n" + modifierValue
        };
        PublishMidiMessage(msg);
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
                var msg = new MidiMessage
                {
                    event_type = "display",
                    info = "No mapping: " + midiMsg.ToString()
                };
                PublishMidiMessage(msg);
                _logger.LogWarning("Couldn't find mapping for: {midiMsg}", midiMsg.ToString());
                return;
            }

            SetEntityValue(mapping, midiMsg);
            //PublishAllMappings();
        }
        catch (Exception e)
        {
            _logger.LogError("Error handling midi message: {ErrorMessage}", e.Message);
        }
    }

    private async Task SetEntityValue(EntityMapping mapping, MidiMessage msg)
    {
        var value = msg.value;
        if (mapping.ControlEntity is null)
        {
            // only execute once (button presses are sent on press and release!)
            if (value == 0)
                return;
            switch (mapping.Options?.SpecialAttribute?.ToLowerInvariant())
            {
                case "reset":
                    InitializeAllControls();
                    await PublishAllMappings(sendInfo: false);
                    _ = SendCurrentStatus(force: true);
                    return;
                case "sinewave":
                    var controller = new FaderWaveController();
                    for (int frame = 0; frame < 100; frame++)
                    {
                        var values = controller.GetFaderValues();
                        for (int i=0; i<values.Length; i++)
                        {
                            var sineMsg = new MidiMessage
                            {
                                event_type = "fader",
                                channel = i+1,
                                value = values[i],
                            };
                            PublishMidiMessage(sineMsg);
                        }
                        System.Threading.Thread.Sleep(50); // 50ms delay between updates
                    }
                    return;
            }
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

    private async Task EntityStateChanged(EntityMapping mapping)
    {
        var msg = await CreateMidiMessage(mapping);
        if (msg is not null)
            PublishMidiMessage(msg);
    }

    private async Task<MidiMessage?> CreateMidiMessage(EntityMapping mapping, int overrideValue = int.MinValue, bool sendInfo = true)
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
            bool onoff = _modifiers.TryGetValue(modifierName, out var value1) && value1.Item1 == modifierValue;
            return new MidiMessage
            {
                event_type = "led",
                channel = 0,
                note = mapping.MidiControl.Note,
                value = onoff ? 1 : 0,
                info = sendInfo ? (onoff ? modifierName + ": " + modifierValue  : "") : ""
            };
        }

        // don't output if mapping has modifier and other modifier is currently selected
        if (_modifiers.TryGetValue(modifierName, out var value) && value.Item1 != modifierValue)
            return null;

        string infoText = "\n\n";
        string imageString = String.Empty;
        infoText += mapping.ControlEntity.TryGetAttribute("friendly_name", out var friendlyName) ? friendlyName + ":\n" : "\n";
        if (sendInfo)
        {
            var image = await CreateEntityImage(mapping.ControlEntity);
            using var memoryStream = new MemoryStream();
            image.SaveAsPng(memoryStream);
            byte[] bytes = memoryStream.ToArray();
            imageString = Convert.ToBase64String(bytes);
            #if DEBUG
            File.WriteAllBytes(@"D:\TEMP\NetDeamon\display.png", memoryStream.ToArray());
            #else
            File.WriteAllBytes("/config/www/display.png", memoryStream.ToArray());
            #endif
        }
        switch (mapping.ControlEntity.GetEntityPlatform().ToLowerInvariant())
        {
            case "cover":
                if (!mapping.ControlEntity.TryGetCoverPosition(out int position))
                    return null;
                infoText += position;

                return new MidiMessage
                {
                    event_type = "fader",
                    channel = mapping.MidiControl?.Channel ?? 0,
                    value = mapping?.Options?.Invert == true ? 100 - position : position,
                    info = sendInfo ? infoText : "",
                    image = imageString
                };
            case "light":
                if (!mapping.ControlEntity.TryGetStateValue(out bool on))
                    return null;
                infoText += mapping.ControlEntity.TryGetStateValue(out string stateValue) ? stateValue : "";
                // simple light with button - only on/off
                if (mapping.Options?.RangeParameter is null)
                {
                    return new MidiMessage
                    {
                        event_type = "led",
                        channel = 0,
                        note = mapping.MidiControl.Note,
                        value = on ? 1 : 0,
                        info = sendInfo ? infoText : "",
                        image = imageString
                    };
                }

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
                        controller = mapping.Options?.RangeOutput?.Controller ?? 0,
                        value = on ? ConvertRange(currentBrightness, newMax: _config.Mqtt.RadialNumLeds) + 200 : 0,
                        info = sendInfo ? infoText + "\n" + "Brightness: " +  currentBrightness : "",
                        image = imageString
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
        _lastMessageSent =  DateTime.UtcNow;
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
    public int? channel { get; init; } = 0;
    public long? timestamp { get; init; } = 0;
    public int value { get; init; }
    public int valueraw { get; init; }
    public int? note { get; init; } = 0;
    public int? controller { get; init; } = 0;
    public string? info { get; init; } = string.Empty;
    public string? image { get; init; } = string.Empty;

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