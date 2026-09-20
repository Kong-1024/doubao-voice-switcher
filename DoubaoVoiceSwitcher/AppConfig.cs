using System.Text.Json;

namespace DoubaoVoiceSwitcher;

internal sealed class AppConfig
{
    public int ActivationDelayMs { get; init; } = 1500;
    public int CommitDelayMs { get; init; } = 1000;
    public bool RestoreWhenAnotherKeyStopsVoice { get; init; } = true;
    public int MaxListeningSeconds { get; init; } = 60;
    public bool PlaySoundFeedback { get; init; } = true;
    public bool CancelOnEsc { get; init; } = true;

    public static AppConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config is null ||
                config.ActivationDelayMs is < 300 or > 5000 ||
                config.CommitDelayMs is < 300 or > 5000 ||
                config.MaxListeningSeconds is < 10 or > 300)
            {
                throw new InvalidDataException("延迟或超时参数超出安全范围。");
            }

            return config;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"无法读取 settings.json：{ex.Message}", ex);
        }
    }
}
