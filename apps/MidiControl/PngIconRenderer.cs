using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO;
using System.Collections.Generic;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using Path = System.IO.Path;

namespace NetDeamon.apps.MidiControl;

public class PngIconRenderer
{
    private readonly string _iconBasePath;
    private readonly Dictionary<string, string> _iconFileMap;
    
    public PngIconRenderer(string iconBasePath = "/config/netdaemon5/ico")
    {
        _iconBasePath = iconBasePath;
        
        _iconFileMap = new Dictionary<string, string>
        {
            { "mdi:thermometer", "thermometer.png" },
            { "mdi:home-thermometer", "home-thermometer.png" },
            { "mdi:lightbulb", "lightbulb.png" },
            { "mdi:lightbulb-on", "lightbulb-on.png" },
            { "mdi:water-percent", "water-percent.png" },
            { "mdi:humidity", "water-percent.png" },
            { "mdi:blinds", "blinds.png" },
            { "mdi:window-shutter", "window-shutter.png" },
            { "mdi:fan", "fan.png" },
            { "mdi:gauge", "gauge.png" }
        };
    }
    
    public Image<Rgba32> RenderIcon(string iconName, int size, Color color)
    {
        var iconFile = _iconFileMap.GetValueOrDefault(iconName.ToLower(), "help-circle.png");
        var iconPath = Path.Combine(_iconBasePath, iconFile);
        
        if (!File.Exists(iconPath))
        {
            return CreatePlaceholder(size, color);
        }
        
        try
        {
            var icon = Image.Load<Rgba32>(iconPath);
            
            // Resize to target size
            icon.Mutate(ctx => ctx.Resize(size, size));
            
            // Colorize the icon
            var targetColor = color.ToPixel<Rgba32>();
            icon.Mutate(ctx =>
            {
                for (int y = 0; y < icon.Height; y++)
                {
                    for (int x = 0; x < icon.Width; x++)
                    {
                        var pixel = icon[x, y];
                        if (pixel.A > 0)
                        {
                            // Apply target color while preserving alpha
                            icon[x, y] = new Rgba32(
                                (byte)(targetColor.R * pixel.A / 255),
                                (byte)(targetColor.G * pixel.A / 255),
                                (byte)(targetColor.B * pixel.A / 255),
                                pixel.A
                            );
                        }
                    }
                }
            });
            
            return icon;
        }
        catch
        {
            return CreatePlaceholder(size, color);
        }
    }
    
    private Image<Rgba32> CreatePlaceholder(int size, Color color)
    {
        var image = new Image<Rgba32>(size, size);
        image.Mutate(ctx =>
        {
            var circle = new EllipsePolygon(size / 2f, size / 2f, size / 3f);
            ctx.Draw(color, 2, circle);
        });
        return image;
    }
}