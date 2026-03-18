using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;

namespace NetDeamon.apps.MidiControl;

public static class ImageSharpExtensions
{
    public static IPath CreateRoundedRectanglePath(int width, int height, float cornerRadius)
    {
        var pathBuilder = new PathBuilder();
        width--;
        height--;

        var radius = 2 * cornerRadius;

        // Make sure the rounded corners are no larger than half the size of the rectangle
        cornerRadius = Math.Min(width * 0.5f, Math.Min(height * 0.5f, cornerRadius));

        // Start drawing path
        pathBuilder.StartFigure();

        // upperBorder
        pathBuilder.AddLine(cornerRadius, 0, width - cornerRadius, 0);

        // Upper right rounded corner
        pathBuilder.AddArc(new RectangleF(width - radius, 0, radius, radius), 0, 270, 90);

        // right line
        pathBuilder.AddLine(width, cornerRadius, width, height - cornerRadius);

        // Lower right rounded corner
        pathBuilder.AddArc(new RectangleF(width - radius, height - radius, radius, radius), 0, 0, 90);

        // lower border
        pathBuilder.AddLine(width - cornerRadius, height, cornerRadius, height);

        // Lower left rounded corner
        pathBuilder.AddArc(new RectangleF(0, height - radius, radius, radius), 0, 90, 90);

        // left line
        pathBuilder.AddLine(0, height - cornerRadius, 0, cornerRadius);

        // Upper left rounded corner
        pathBuilder.AddArc(new RectangleF(0, 0, radius, radius), 0, 180, 90);

        // Close the path to form a complete rectangle
        pathBuilder.CloseFigure();

        return pathBuilder.Build();
    }
    
}