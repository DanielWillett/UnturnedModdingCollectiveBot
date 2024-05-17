using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using System.Text.Json;
using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Services;
public class JsonLiveConfiguration<T> : ILiveConfiguration<T> where T : class, IDefaultable, new()
{
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly object _sync = new object();
    private readonly string _filePath;
    private readonly string? _dirPath;

    private T _config = new T();

    public T Configuraiton
    {
        get
        {
            T config;
            lock (_sync)
                config = _config;
            return config;
        }
    }
    public JsonLiveConfiguration(string filePath, ILogger<JsonLiveConfiguration<T>> logger)
    {
        _filePath = filePath;
        _dirPath = Path.GetDirectoryName(_filePath);
    }
    public T Reload()
    {
        lock (_sync)
        {
            if (_dirPath != null)
                Directory.CreateDirectory(_dirPath);

            if (File.Exists(_filePath))
            {
                _config.SetDefaults();
                WriteConfigurationIntl();
                return _config;
            }

            T? config;

            using (FileStream fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan))
                config = JsonSerializer.Deserialize<T>(fs, _jsonOptions);

            if (config == null)
            {
                (config ??= new T()).SetDefaults();
                WriteConfigurationIntl();
            }

            _config = config;
            return config;
        }
    }
    public void Save()
    {
        lock (_sync)
            WriteConfigurationIntl();
    }
    private void WriteConfigurationIntl()
    {
        using FileStream fs = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan);
        JsonSerializer.Serialize(fs, _config, _jsonOptions);
    }
}
