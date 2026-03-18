using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Path = System.IO.Path;

namespace NetDeamon.apps.MidiControl;

public class IconManager
{
    private const string GitHubSvgBaseUrl = "https://raw.githubusercontent.com/Templarian/MaterialDesign/master/svg";
    private readonly HashSet<string> _attemptedDownloads;
    private readonly HttpClient _httpClient;
    private readonly string _iconDirectory;
    private readonly Dictionary<string, SemaphoreSlim> _iconLocks;
    private readonly SemaphoreSlim _lockDictionaryLock;
    
    public IconManager(string iconDirectory = "/config/netdaemon5/ico")
    {
        _iconDirectory = iconDirectory;
        _httpClient = new HttpClient();
        _attemptedDownloads = new HashSet<string>();
        _iconLocks = new Dictionary<string, SemaphoreSlim>();
        _lockDictionaryLock = new SemaphoreSlim(1, 1);
        
        Directory.CreateDirectory(_iconDirectory);
    }

    public async Task<Image<Rgba32>> GetIconAsync(string iconName, int size, Color color)
    {
        var cleanIconName = ExtractIconName(iconName);
        // Get or create a lock for this specific icon
        SemaphoreSlim iconLock;
        await _lockDictionaryLock.WaitAsync();
        try
        {
            if (!_iconLocks.TryGetValue(cleanIconName, out iconLock))
            {
                iconLock = new SemaphoreSlim(1, 1);
                _iconLocks[cleanIconName] = iconLock;
            }
        }
        finally
        {
            _lockDictionaryLock.Release();
        }
        // Now lock for this specific icon
        await iconLock.WaitAsync();
        try
        {
            var pngPath = Path.Combine(_iconDirectory, $"{cleanIconName}.png");

            if (File.Exists(pngPath)) return await LoadAndColorizeIconAsync(pngPath, size, color);

            var svgPath = Path.Combine(_iconDirectory, $"{cleanIconName}.svg");

            // Download SVG if it doesn't exist
            if (!File.Exists(svgPath))
            {
                if (!_attemptedDownloads.Contains(cleanIconName))
                {
                    _attemptedDownloads.Add(cleanIconName);
                    Console.WriteLine($"Downloading icon: {cleanIconName}");

                    if (!await DownloadIconAsync(cleanIconName, svgPath))
                    {
                        Console.WriteLine($"Icon not found: {cleanIconName}, using placeholder");
                        return CreatePlaceholder(size, color, cleanIconName);
                    }
                }
                else
                {
                    return CreatePlaceholder(size, color, cleanIconName);
                }
            }

            // Convert SVG to PNG
            Console.WriteLine($"Converting {cleanIconName}.svg to PNG...");
            await ConvertSvgToPngAsync(svgPath, pngPath, 512);
            return await LoadAndColorizeIconAsync(pngPath, size, color);
        }
        finally
        {
            iconLock.Release();
        }
    }

    private string ExtractIconName(string iconName)
    {
        if (iconName.StartsWith("mdi:")) return iconName.Substring(4);
        return iconName;
    }

    private async Task<bool> DownloadIconAsync(string iconName, string outputPath)
    {
        try
        {
            var url = $"{GitHubSvgBaseUrl}/{iconName}.svg";
            Console.WriteLine($"Downloading from: {url}");

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var svgContent = await response.Content.ReadAsStringAsync();
                await File.WriteAllTextAsync(outputPath, svgContent);
                Console.WriteLine($"Successfully downloaded: {iconName}.svg");
                return true;
            }

            Console.WriteLine($"Failed to download {iconName}: {response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading {iconName}: {ex.Message}");
            return false;
        }
    }

    private async Task ConvertSvgToPngAsync(string svgPath, string pngPath, int size)
    {
        try
        {
            var svgContent = await File.ReadAllTextAsync(svgPath);
            var doc = XDocument.Parse(svgContent);

            var ns = doc.Root.Name.Namespace;
            var pathElement = doc.Descendants(ns + "path").FirstOrDefault()
                              ?? doc.Descendants("path").FirstOrDefault();

            if (pathElement == null)
                throw new Exception("No path found in SVG");

            var pathData = pathElement.Attribute("d")?.Value;
            if (string.IsNullOrEmpty(pathData))
                throw new Exception("No path data found");

            Console.WriteLine($"Parsing path: {pathData}");
            var normalizedPath = NormalizeSvgPath(pathData);
            Console.WriteLine($"Normalized path: {normalizedPath}");

            // Use ImageSharp's built-in SVG path parser
            if (!SixLabors.ImageSharp.Drawing.Path.TryParseSvgPath(normalizedPath, out var path))
                throw new Exception("Failed to parse SVG path");

            // Should be a single path
            var bounds = path.Bounds;

            Console.WriteLine($"Path bounds: X=[{bounds.Left}, {bounds.Right}], Y=[{bounds.Top}, {bounds.Bottom}]");
            Console.WriteLine($"Path size: {bounds.Width} x {bounds.Height}");

            // Calculate scale and offset to fit with padding
            var padding = size * 0.1f;
            var availableSize = size - 2 * padding;
            var scale = availableSize / Math.Max(bounds.Width, bounds.Height);

            var scaledWidth = bounds.Width * scale;
            var scaledHeight = bounds.Height * scale;

            // Calculate offset to center the scaled path
            var offsetX = padding + (availableSize - scaledWidth) / 2 - bounds.Left * scale;
            var offsetY = padding + (availableSize - scaledHeight) / 2 - bounds.Top * scale;

            Console.WriteLine($"Scaled size: {scaledWidth} x {scaledHeight}");
            Console.WriteLine($"Final scale: {scale}, offset: ({offsetX}, {offsetY})");

            // Transform the path
            var transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offsetX, offsetY);
            var transformedPath = path.Transform(transform);

            // Create image and draw
            using var image = new Image<Rgba32>(size, size);
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.Transparent);
                ctx.Fill(Color.White, transformedPath);
            });

            await image.SaveAsPngAsync(pngPath);
            Console.WriteLine($"Converted {Path.GetFileName(svgPath)} to PNG");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error converting SVG: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");

            // Create simple fallback
            using var image = new Image<Rgba32>(size, size);
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.Transparent);
                var rect = new RectangularPolygon(size * 0.3f, size * 0.1f, size * 0.4f, size * 0.8f);
                ctx.Fill(Color.White, rect);
            });
            await image.SaveAsPngAsync(pngPath);
        }
    }

    private string NormalizeSvgPath(string pathData)
    {
        // Add space before each command letter (M, L, H, V, C, S, Q, T, A, Z)
        var result = new StringBuilder();
        var commands = new HashSet<char>
            { 'M', 'm', 'L', 'l', 'H', 'h', 'V', 'v', 'C', 'c', 'S', 's', 'Q', 'q', 'T', 't', 'A', 'a', 'Z', 'z' };

        for (var i = 0; i < pathData.Length; i++)
        {
            var c = pathData[i];

            if (commands.Contains(c))
            {
                // Add space before command (unless it's the first character)
                if (i > 0 && pathData[i - 1] != ' ') result.Append(' ');
                result.Append(c);
                // Add space after command
                // if (i < pathData.Length - 1 && pathData[i + 1] != ' ')
                // {
                //     result.Append(' ');
                // }
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private async Task<Image<Rgba32>> LoadAndColorizeIconAsync(string pngPath, int size, Color color)
    {
        var icon = await Image.LoadAsync<Rgba32>(pngPath);

        icon.Mutate(ctx => ctx.Resize(size, size));

        var targetColor = color.ToPixel<Rgba32>();
        icon.Mutate(ctx =>
        {
            for (var y = 0; y < icon.Height; y++)
            for (var x = 0; x < icon.Width; x++)
            {
                var pixel = icon[x, y];
                if (pixel.A > 0)
                    icon[x, y] = new Rgba32(
                        (byte)(targetColor.R * pixel.A / 255),
                        (byte)(targetColor.G * pixel.A / 255),
                        (byte)(targetColor.B * pixel.A / 255),
                        pixel.A
                    );
            }
        });

        return icon;
    }

    private Image<Rgba32> CreatePlaceholder(int size, Color color, string iconName = "")
    {
        var image = new Image<Rgba32>(size, size);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Transparent);

            var circle = new EllipsePolygon(size / 2f, size / 2f, size / 3f);
            ctx.Draw(color, 2, circle);

            if (!string.IsNullOrEmpty(iconName) && iconName.Length > 0)
                try
                {
                    var font = SystemFonts.CreateFont("Arial", size / 3f);
                    var text = iconName.Substring(0, 1).ToUpper();

                    var textOptions = new RichTextOptions(font)
                    {
                        Origin = new PointF(size / 2f, size / 2f),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    ctx.DrawText(textOptions, text, color);
                }
                catch
                {
                    // Font rendering failed, just use circle
                }
        });
        return image;
    }
}