using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using Path = System.IO.Path;

namespace AlibabaTreasure.Server;

/// <summary>
/// Owns <c>config/config.json</c>: reads it once, hands the live copy to every consumer, and
/// writes it back whenever the F12 plugin changes something.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is static because the Harmony patches that roll the spawn chances are
/// static too and have no DI scope to be handed this class through.
/// </remarks>
[Injectable(InjectionType.Singleton)]
public class TreasureConfigStore(ISptLogger<TreasureConfigStore> logger, ModHelper modHelper)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Keep "—" and Korean as written instead of \uXXXX escapes (kb 10.6).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object Gate = new();
    private static volatile TreasureSettings _current = TreasureSettings.Defaults();
    private bool _loaded;

    /// <summary>The settings in force right now. Never null, always normalised.</summary>
    public static TreasureSettings Current => _current;

    private string ConfigPath => Path.Combine(
        modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "config", "config.json");

    public TreasureSettings Load()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return _current;
            }

            _loaded = true;
            var path = ConfigPath;

            try
            {
                if (File.Exists(path))
                {
                    var parsed = JsonSerializer.Deserialize<TreasureSettings>(File.ReadAllText(path), JsonOptions);
                    _current = (parsed ?? new TreasureSettings()).Normalised();
                }
                else
                {
                    _current = TreasureSettings.Defaults();
                    Save(_current);
                    logger.Info($"{TreasureMod.LogPrefix} config.json was missing - wrote the defaults to {path}.");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"{TreasureMod.LogPrefix} could not read {path}; using defaults this session.", ex);
                _current = TreasureSettings.Defaults();
            }

            return _current;
        }
    }

    /// <summary>Normalises, installs and saves <paramref name="incoming"/>. Returns what was kept.</summary>
    public (TreasureSettings Previous, TreasureSettings Applied, bool Saved) Replace(TreasureSettings incoming)
    {
        lock (Gate)
        {
            var previous = _current;
            var applied = new TreasureSettings
            {
                Keycard = incoming.Keycard ?? previous.Keycard,
                Key = incoming.Key ?? previous.Key,
                MarkedKey = incoming.MarkedKey ?? previous.MarkedKey,
                LabsPass = incoming.LabsPass ?? previous.LabsPass,
            }.Normalised();

            _current = applied;
            return (previous, applied, Save(applied));
        }
    }

    private bool Save(TreasureSettings settings)
    {
        var path = ConfigPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"{TreasureMod.LogPrefix} could not save {path}; the change is live but will not survive a restart.", ex);
            return false;
        }
    }
}
