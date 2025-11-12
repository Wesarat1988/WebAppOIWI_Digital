using System.Globalization;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using WepAppOIWI_Digital.Stamps;

namespace WepAppOIWI_Digital.Services;

public interface IPdfStampService
{
    Task<byte[]> ApplyStampAsync(byte[] pdfBytes, StampMode mode, DateOnly? date, CancellationToken cancellationToken = default);
}

public sealed class PdfStampService : IPdfStampService
{
    private const string ThaiFontFamily = "Noto Sans Thai";
    private const string ThaiFontResourceName = "NotoSansThai-Regular.ttf";
    private const double BoxPadding = 6d;
    private const double BoxWidth = 320d;
    private const double BoxHeight = 48d;
    private const double TitleFontSize = 11d;
    private const double SubtitleFontSize = 10d;
    private static readonly XBrush BoxBrush = new XSolidBrush(XColor.FromArgb(235, 245, 255));
    private static readonly XPen BoxPen = new(XColor.FromArgb(40, 98, 255), 1);
    private static readonly XPdfFontOptions PdfFontOptions = new(PdfFontEncoding.Unicode);
    private static readonly Lazy<byte[]?> ThaiFontBytes = new(LoadFontBytes, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object ResolverLock = new();
    private static bool _fontResolverInitialized;

    private readonly ILogger<PdfStampService> _logger;

    public PdfStampService(ILogger<PdfStampService> logger)
    {
        _logger = logger;
        EnsureFontResolverRegistered();

        if (ThaiFontBytes.Value is null)
        {
            _logger.LogWarning(
                "Thai font resource '{Font}' was not found. PDF stamps will fall back to standard fonts and may not render Thai text correctly. Place the font file in Resources/Fonts or App_Data/fonts.",
                ThaiFontResourceName);
        }
    }

    public Task<byte[]> ApplyStampAsync(byte[] pdfBytes, StampMode mode, DateOnly? date, CancellationToken cancellationToken = default)
    {
        if (pdfBytes is null)
        {
            throw new ArgumentNullException(nameof(pdfBytes));
        }

        if (mode == StampMode.None || pdfBytes.Length == 0)
        {
            return Task.FromResult(pdfBytes);
        }

        using var inputStream = new MemoryStream(pdfBytes, writable: false);
        using var document = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

        if (document.PageCount == 0)
        {
            return Task.FromResult(pdfBytes);
        }

        var stampText = ComposeTitle(mode);
        var dateText = ComposeSubtitle(mode, date);

        for (var i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[i];
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);

            var margin = 16d;
            var x = page.Width - BoxWidth - margin;
            var y = margin;
            var rect = new XRect(x, y, BoxWidth, BoxHeight);

            graphics.DrawRectangle(BoxPen, BoxBrush, rect);

            var titleFont = CreateFont(TitleFontSize, XFontStyle.Bold);
            var subtitleFont = CreateFont(SubtitleFontSize, XFontStyle.Regular);

            var titleRect = new XRect(rect.X + BoxPadding, rect.Y + 6, rect.Width - (2 * BoxPadding), 18);
            graphics.DrawString(stampText, titleFont, XBrushes.Black, titleRect, XStringFormats.TopLeft);

            var subtitleRect = new XRect(rect.X + BoxPadding, rect.Y + 24, rect.Width - (2 * BoxPadding), 18);
            graphics.DrawString(dateText, subtitleFont, XBrushes.Black, subtitleRect, XStringFormats.TopLeft);
        }

        using var outputStream = new MemoryStream();
        document.Save(outputStream, closeStream: false);
        return Task.FromResult(outputStream.ToArray());
    }

    private static string ComposeTitle(StampMode mode)
        => mode switch
        {
            StampMode.MasterControl => "Master Control วันที่ปั้ม IE/PE DCFAN",
            StampMode.ValidUnitTemporary => "VALID UNIT วันที่หมดอายุ TEMPORARY",
            _ => string.Empty
        };

    private static string ComposeSubtitle(StampMode mode, DateOnly? date)
    {
        var formatted = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
        return mode switch
        {
            StampMode.MasterControl => $"วันที่: {formatted}",
            StampMode.ValidUnitTemporary => $"หมดอายุ: {formatted}",
            _ => formatted
        };
    }

    private XFont CreateFont(double size, XFontStyle style)
    {
        var familyName = ThaiFontBytes.Value is not null ? ThaiFontFamily : "Helvetica";
        try
        {
            return new XFont(familyName, size, style, PdfFontOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to standard font for PDF stamp text.");
            return new XFont("Helvetica", size, style, PdfFontOptions);
        }
    }

    private static void EnsureFontResolverRegistered()
    {
        if (_fontResolverInitialized)
        {
            return;
        }

        lock (ResolverLock)
        {
            if (_fontResolverInitialized)
            {
                return;
            }

            var fontBytes = ThaiFontBytes.Value;
            if (fontBytes is null || fontBytes.Length == 0)
            {
                _fontResolverInitialized = true;
                return;
            }

            var fallback = GlobalFontSettings.FontResolver;
            GlobalFontSettings.FontResolver = new EmbeddedFontResolver(fontBytes, fallback);
            _fontResolverInitialized = true;
        }
    }

    private static byte[]? LoadFontBytes()
    {
        try
        {
            var assembly = typeof(PdfStampService).GetTypeInfo().Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(ThaiFontResourceName, StringComparison.OrdinalIgnoreCase));

            if (resourceName is not null)
            {
                using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                if (resourceStream is not null)
                {
                    using var buffer = new MemoryStream();
                    resourceStream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }

            foreach (var candidate in EnumerateCandidateFontPaths())
            {
                if (File.Exists(candidate))
                {
                    return File.ReadAllBytes(candidate);
                }
            }
        }
        catch
        {
            // ignore and fall back to standard fonts
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateFontPaths()
    {
        var baseDir = AppContext.BaseDirectory;

        yield return Path.Combine(baseDir, "Resources", "Fonts", ThaiFontResourceName);
        yield return Path.Combine(baseDir, "App_Data", "fonts", ThaiFontResourceName);
        yield return Path.Combine(baseDir, ThaiFontResourceName);

        var current = Directory.GetCurrentDirectory();
        if (!string.Equals(current, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(current, "Resources", "Fonts", ThaiFontResourceName);
            yield return Path.Combine(current, ThaiFontResourceName);
        }
    }

    private sealed class EmbeddedFontResolver : IFontResolver
    {
        private readonly byte[] _regular;
        private readonly IFontResolver? _fallback;

        public EmbeddedFontResolver(byte[] regular, IFontResolver? fallback)
        {
            _regular = regular;
            _fallback = fallback;
        }

        public string DefaultFontName => ThaiFontFamily;

        public byte[] GetFont(string faceName)
        {
            return faceName switch
            {
                ThaiFontFamily => _regular,
                ThaiFontFamily + "#b" => _regular,
                _ when _fallback is not null => _fallback.GetFont(faceName),
                _ => _regular
            };
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (string.Equals(familyName, ThaiFontFamily, StringComparison.OrdinalIgnoreCase)
                || string.Equals(familyName, "NotoSansThai", StringComparison.OrdinalIgnoreCase))
            {
                var faceName = isBold ? ThaiFontFamily + "#b" : ThaiFontFamily;
                return new FontResolverInfo(faceName, isBold, isItalic);
            }

            if (_fallback is not null)
            {
                return _fallback.ResolveTypeface(familyName, isBold, isItalic);
            }

            return PlatformFontResolver.ResolveTypeface(familyName, isBold, isItalic);
        }
    }
}
