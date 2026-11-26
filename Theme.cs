using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaController;

/// <summary>Colores del widget: fondo según la portada y texto secundario.</summary>
public static class Theme
{
    /// <summary>Fondo por defecto (sin portada).</summary>
    public static readonly Color DefaultBg = Color.FromRgb(0x1E, 0x1E, 0x1E);

    private const double MinContrast = 4.5;   // contraste mínimo para que el texto claro se lea
    private const double Margin = 0.90;       // oscurecimiento extra, igual para todas las portadas

    /// <summary>Color promedio de la portada (se calcula una vez por canción, sobre una imagen chica).</summary>
    public static Color? AverageColor(BitmapSource bmp)
    {
        try
        {
            var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            if (w <= 0 || h <= 0) return null;

            int stride = w * 4;
            var px = new byte[stride * h];
            converted.CopyPixels(px, stride, 0);

            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i + 3 < px.Length; i += 16)   // 1 de cada 4 píxeles
            {
                if (px[i + 3] < 16) continue;             // ignora transparentes
                b += px[i];
                g += px[i + 1];
                r += px[i + 2];
                n++;
            }
            if (n == 0) return null;
            return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Regla del fondo: 1) oscurecer solo lo mínimo para que el texto se lea;
    /// 2) oscurecer un poco más, con el mismo margen para todas las portadas.
    /// </summary>
    public static Color BackgroundFor(Color avg)
    {
        double r = avg.R, g = avg.G, b = avg.B;
        double f = 1.0;

        if (ContrastWithLightText(Luminance(r, g, b)) < MinContrast)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 16; i++)
            {
                double mid = (lo + hi) / 2;
                if (ContrastWithLightText(Luminance(r * mid, g * mid, b * mid)) >= MinContrast) lo = mid;
                else hi = mid;
            }
            f = lo;
        }

        f *= Margin;
        return Color.FromRgb((byte)Math.Round(r * f), (byte)Math.Round(g * f), (byte)Math.Round(b * f));
    }

    /// <summary>Texto secundario (artista y tiempos): más claro mientras más claro es el fondo.</summary>
    public static Color SecondaryText(Color bg)
    {
        double lum = Luminance(bg.R, bg.G, bg.B);
        double t = Math.Clamp(lum / 0.12, 0, 1);
        byte v = (byte)Math.Round(0xB4 + (0xF0 - 0xB4) * t);
        return Color.FromRgb(v, v, v);
    }

    private static double Linear(double c)
    {
        c /= 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(double r, double g, double b)
        => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    private static double ContrastWithLightText(double bgLuminance)
        => (Luminance(0xF0, 0xF0, 0xF0) + 0.05) / (bgLuminance + 0.05);
}

/// <summary>Reconoce la app de origen a partir del identificador que entrega Windows.</summary>
public static class SourceInfo
{
    public static string FriendlyName(string? aumid)
    {
        string s = aumid ?? "";
        string l = s.ToLowerInvariant();

        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("youtube")) return "YouTube";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("msedge") || l.Contains("edge")) return "Edge";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("opera")) return "Opera";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("zune") || l.Contains("mediaplayer")) return "Reproductor";

        int bang = s.IndexOf('!');
        if (bang > 0) s = s.Substring(0, bang);
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
        int underscore = s.IndexOf('_');
        if (underscore > 0) s = s.Substring(0, underscore);
        return string.IsNullOrWhiteSpace(s) ? "Desconocida" : s;
    }

    /// <summary>
    /// Color de la barra de progreso. Prioridad: color de la app (Spotify, YouTube),
    /// luego violeta si es video, y si no, blanco.
    /// </summary>
    public static Color Accent(string? aumid, bool isVideo)
    {
        string l = (aumid ?? "").ToLowerInvariant();
        if (l.Contains("spotify")) return Color.FromRgb(0x1D, 0xB9, 0x54);
        if (l.Contains("youtube")) return Color.FromRgb(0xFF, 0x00, 0x00);
        if (isVideo) return Color.FromRgb(0xB7, 0x9C, 0xFF);
        return Colors.White;
    }
}
