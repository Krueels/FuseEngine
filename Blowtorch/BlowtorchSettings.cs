using System.Text.Json;

namespace Blowtorch;

public sealed class BlowtorchSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FuseExecutablePath { get; set; } = "";

    public static string StoragePath
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FuseEngine",
                "Blowtorch");
            return Path.Combine(directory, "settings.json");
        }
    }

    public static BlowtorchSettings Load()
    {
        try
        {
            if (!File.Exists(StoragePath))
                return new BlowtorchSettings();

            return JsonSerializer.Deserialize<BlowtorchSettings>(
                       File.ReadAllText(StoragePath), JsonOptions)
                   ?? new BlowtorchSettings();
        }
        catch (Exception ex)
        {
            Fuse.Core.Logger.Warn($"Blowtorch settings could not be loaded: {ex.Message}");
            return new BlowtorchSettings();
        }
    }

    public bool Save(out string error)
    {
        error = "";
        try
        {
            string? directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(StoragePath, JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not save Blowtorch settings: {ex.Message}";
            Fuse.Core.Logger.Warn(error);
            return false;
        }
    }
}
