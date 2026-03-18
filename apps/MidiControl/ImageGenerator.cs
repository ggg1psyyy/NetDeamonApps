using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Path = SixLabors.ImageSharp.Drawing.Path;

namespace NetDeamon.apps.MidiControl;
public class ImageConfig
{
    // Image dimensions
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 240;
    
    // Colors
    public Color BackgroundColor { get; set; } = Color.ParseHex("#1E1E1E"); // Dark gray
    public Color TextColor { get; set; } = Color.ParseHex("#B0B0B0"); // Light gray
    public Color IconColor { get; set; } = Color.ParseHex("#64B5F6"); // Light blue
    public Color ValueColor { get; set; } = Color.White;
    public Color UnitColor { get; set; } = Color.ParseHex("#B0B0B0"); // Light gray
    public Color GraphLineColor { get; set; } = Color.ParseHex("#FFA726"); // Orange
    public Color GraphFillColor { get; set; } = Color.ParseHex("#FFA72640"); // Semi-transparent orange
    
    // Layout proportions (0.0 to 1.0)
    public float IconSize { get; set; } = 0.15f; // 15% of height
    public float NameFontSize { get; set; } = 0.08f; // 8% of height
    public float ValueFontSize { get; set; } = 0.25f; // 25% of height (large!)
    public float UnitFontSize { get; set; } = 0.10f; // 10% of height
    
    // Spacing proportions
    public float PaddingLeft { get; set; } = 0.06f; // 6% of width
    public float PaddingTop { get; set; } = 0.08f; // 8% of height
    public float PaddingRight { get; set; } = 0.06f; // 6% of width
    public float IconRightMargin { get; set; } = 0.08f; // Space between icon and value
    
    // Graph settings
    public bool ShowGraph { get; set; } = true;
    public float GraphHeight { get; set; } = 0.30f; // 30% of image height
    public float GraphBottomMargin { get; set; } = 0.04f; // 4% from bottom
    public float GraphSideMargin { get; set; } = 0.06f; // 6% from sides
    public float GraphLineWidth { get; set; } = 3f;
    public bool GraphFillEnabled { get; set; } = true;
    
    // Font paths
    public string RegularFontPath { get; set; } = "/config/netdaemon5/fonts/Roboto-Regular.ttf";
    public string LightFontPath { get; set; } = "/config/netdaemon5/fonts/Roboto-Light.ttf";
    public string IconPath { get; set; } = "/config/netdaemon5/ico";
    
    // Corner radius for rounded corners (optional)
    public float CornerRadius { get; set; } = 8f;
}

public class ImageGenerator
{
    private readonly FontCollection _fontCollection;
    private readonly ImageConfig _config;
    private readonly IconManager _iconManager;
    
    // MDI icon mapping
    private static readonly Dictionary<string, string> MdiIconMap = new()
    {
        { "mdi:thermometer", "\uf2c7" }, // fa-thermometer-half
        { "mdi:lightbulb", "\uf0eb" }, // fa-lightbulb
        { "mdi:water-percent", "\uf043" }, // fa-droplet
        { "mdi:fan", "\uf863" }, // fa-fan
        { "mdi:home-thermometer", "\uf2c7" },
        { "mdi:help-circle", "\uf059" } // fa-circle-question
    };
    
    public ImageGenerator(ImageConfig config = null)
    {
        _config = config ?? new ImageConfig();
        _fontCollection = new FontCollection();
        _iconManager = new IconManager(_config.IconPath);
        #if DEBUG
        _iconManager = new IconManager(@"D:\TEMP\NetDeamon\ico");
        #else
        _iconManager = new IconManager(_config.IconPath);
        #endif
    }
    
    public async Task<Image<Rgba32>> CreateEntityImage(
        string? name,
        string iconName,
        string value,
        string? unit = "",
        List<float> graphData = null)
    {
        var image = new Image<Rgba32>(_config.Width, _config.Height);
        
        // Load fonts
        Font nameFont = LoadFont(_config.LightFontPath ?? _config.RegularFontPath, _config.Height * _config.NameFontSize);
        Font valueFont = LoadFont(_config.RegularFontPath, _config.Height * _config.ValueFontSize);
        Font unitFont = LoadFont(_config.LightFontPath ?? _config.RegularFontPath, _config.Height * _config.UnitFontSize);
        
        image.Mutate(ctx =>
        {
            // Background with optional rounded corners
            if (_config.CornerRadius > 0)
            {
                // var rect = new RectangleF(0, 0, _config.Width, _config.Height);
                // var corners = rect.ToRoundedRectangle(_config.CornerRadius);
                ctx.Fill(_config.BackgroundColor, ImageSharpExtensions.CreateRoundedRectanglePath(_config.Width, _config.Height, _config.CornerRadius));
            }
            else
            {
                ctx.Fill(_config.BackgroundColor);
            }
            
            // Calculate positions
            float paddingLeft = _config.Width * _config.PaddingLeft;
            float paddingTop = _config.Height * _config.PaddingTop;
            float paddingRight = _config.Width * _config.PaddingRight;
            
            // Draw name (top left)
            var nameOptions = new RichTextOptions(nameFont)
            {
                Origin = new PointF(paddingLeft + (_config.Width * _config.IconSize), paddingTop),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            ctx.DrawText(nameOptions, name, _config.TextColor);

            float valueY = paddingTop + (_config.Height * _config.NameFontSize) + (_config.Height * 0.05f);
            
            var valueOptions = new RichTextOptions(valueFont)
            {
                Origin = new PointF(paddingLeft, valueY),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            ctx.DrawText(valueOptions, value, _config.ValueColor);
            
            // Draw unit next to value (slightly smaller and lighter)
            if (!string.IsNullOrEmpty(unit))
            {
                var valueSize = TextMeasurer.MeasureSize(value, valueOptions);
                float unitX = paddingLeft + valueSize.Width + (_config.Width * 0.02f);
                float unitY = valueY + valueSize.Height - (_config.Height * _config.UnitFontSize) - (_config.Height * 0.02f);
                
                var unitOptions = new RichTextOptions(unitFont)
                {
                    Origin = new PointF(unitX, unitY),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                ctx.DrawText(unitOptions, unit, _config.UnitColor);
            }
            
            // Draw graph if enabled and data provided
            if (_config.ShowGraph && graphData != null && graphData.Count > 1)
            {
                DrawGraph(ctx, graphData);
            }
        });
        await DrawIconAsync(image, iconName, _config.IconColor);
        return image;
    }
    private async Task DrawIconAsync(Image<Rgba32> image, string iconName, Color color)
    {
        int iconSize = (int)(_config.Height * _config.IconSize);
        float paddingLeft = _config.Width * _config.PaddingLeft;
        float paddingTop = _config.Height * _config.PaddingTop;
        
        float iconX = paddingLeft + (iconSize / 2f) + (_config.Width * 0.05f);
        float iconY = paddingTop + (iconSize / 2f) - (_config.Height * 0.05f);
        
        // Get icon from manager (downloads if needed)
        using var iconImage = await _iconManager.GetIconAsync(iconName, iconSize, color);
        
        // Calculate draw position (right-aligned)
        float drawX = iconX - iconSize;
        float drawY = iconY - (iconSize / 2f);
        
        image.Mutate(ctx =>
        {
            ctx.DrawImage(iconImage, new Point((int)drawX, (int)drawY), 1f);
        });
    }

    private void DrawGraph(IImageProcessingContext ctx, List<float> data)
    {
        if (data.Count < 2) return;
    
        // Calculate graph area
        float graphHeight = _config.Height * _config.GraphHeight;
        float graphBottom = _config.Height - (_config.Height * _config.GraphBottomMargin);
        float graphTop = graphBottom - graphHeight;
        float graphLeft = _config.Width * _config.GraphSideMargin;
        float graphRight = _config.Width - (_config.Width * _config.GraphSideMargin);
        float graphWidth = graphRight - graphLeft;
    
        // Normalize data
        float minValue = data.Min();
        float maxValue = data.Max();
        float range = maxValue - minValue;
    
        if (range == 0) range = 1; // Avoid division by zero
    
        // Add some padding to the graph (don't use full height)
        float graphPadding = graphHeight * 0.1f;
        float usableHeight = graphHeight - (2 * graphPadding);
    
        // Create points for line
        var points = new List<PointF>();
        float stepX = graphWidth / (data.Count - 1);
    
        for (int i = 0; i < data.Count; i++)
        {
            float normalizedValue = (data[i] - minValue) / range;
            float x = graphLeft + (i * stepX);
            float y = graphBottom - graphPadding - (normalizedValue * usableHeight);
            points.Add(new PointF(x, y));
        }
    
        // Draw filled area if enabled
        if (_config.GraphFillEnabled)
        {
            var fillPoints = new List<PointF>();
            fillPoints.Add(new PointF(graphLeft, graphBottom)); // Bottom left
            fillPoints.AddRange(points); // Graph line
            fillPoints.Add(new PointF(graphRight, graphBottom)); // Bottom right
        
            var polygon = new Polygon(new LinearLineSegment(fillPoints.ToArray()));
            ctx.Fill(_config.GraphFillColor, polygon);
        }
    
        // Draw line (simple linear segments - works for any number of points)
        var path = new Path(new LinearLineSegment(points.ToArray()));
        ctx.Draw(_config.GraphLineColor, _config.GraphLineWidth, path);
    }
    
    private Font LoadFont(string path, float size)
    {
        try
        {
            if (File.Exists(path))
            {
                var fontFamily = _fontCollection.Add(path);
                return fontFamily.CreateFont(size, FontStyle.Regular);
            }
            else
            {
                // Fallback to system font
                return SystemFonts.CreateFont("Arial", size, FontStyle.Regular);
            }
        }
        catch
        {
            // Ultimate fallback
            return SystemFonts.CreateFont("Arial", size, FontStyle.Regular);
        }
    }
}
