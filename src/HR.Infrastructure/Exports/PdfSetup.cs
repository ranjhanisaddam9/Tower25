using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace HR.Infrastructure.Exports;

/// <summary>
/// One-time QuestPDF setup (M9): the Community licence (free below USD 1M annual revenue, see README) and the embedded
/// Plus Jakarta Sans TTFs (SIL OFL 1.1, Fonts/OFL.txt). Called at startup; safe to call more than once.
/// </summary>
public static class PdfSetup
{
    public const string FontFamily = "Plus Jakarta Sans";

    private static readonly Lock Gate = new();
    private static bool _configured;

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            QuestPDF.Settings.License = LicenseType.Community;

            // A name with a character the font lacks falls back to an installed font instead of failing the download.
            QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;

            var assembly = typeof(PdfSetup).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("HR.Infrastructure.Fonts.", StringComparison.Ordinal) && n.EndsWith(".ttf", StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                FontManager.RegisterFont(stream);
            }

            _configured = true;
        }
    }
}
