using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MediaController;

/// <summary>Posición y tamaño guardados en %AppData%\MediaController\settings.json</summary>
public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 640;
    public double Height { get; set; } = 48;
    public double InfoWidth { get; set; } = 170;   // ancho del área de título/artista (separador)

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MediaController", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void ForgetPosition()
    {
        Left = null;
        Top = null;
        Save();
    }
}

/// <summary>Iniciar con Windows (clave Run del usuario actual, no requiere administrador).</summary>
public static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MediaController";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
            if (enabled)
                key.SetValue(ValueName, "\"" + (Environment.ProcessPath ?? "") + "\"");
            else
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
