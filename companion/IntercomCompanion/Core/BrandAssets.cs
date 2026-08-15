using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace IntercomCompanion.Core;

/// <summary>Loads the brand icon and logo bitmaps from embedded resources exactly once.</summary>
internal static class BrandAssets
{
    private static Icon? appIcon;
    private static Image? lockup;
    private static Image? mark;

    /// <summary>Multi-frame application icon (16 → 256 px).</summary>
    public static Icon AppIcon => appIcon ??= Load("IntercomCompanion.app.ico", stream => new Icon(stream));

    /// <summary>Horizontal lockup, 1280 × 320, for a future About box.</summary>
    public static Image Lockup => lockup ??= Load("IntercomCompanion.logo-lockup.png", Image.FromStream);

    /// <summary>Square mark, 256 × 256, transparent background.</summary>
    public static Image Mark => mark ??= Load("IntercomCompanion.logo-mark-256.png", Image.FromStream);

    /// <summary>Returns a precise icon frame so Windows does not choose and scale a mismatched size.</summary>
    public static Icon AppIconAt(int px) => new(AppIcon, new Size(px, px));

    private static T Load<T>(string logicalName, Func<Stream, T> factory)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded brand resource '{logicalName}' is missing. Check the <EmbeddedResource LogicalName=…> entries in IntercomCompanion.csproj.");
        }

        return factory(stream);
    }
}
