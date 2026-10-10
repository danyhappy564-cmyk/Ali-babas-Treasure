using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace AlibabaTreasure.Server;

/// <summary>
/// Everything one treasure item can be tuned by. The same shape is the config file on disk, the
/// body the F12 plugin posts, and the answer the plugin reads back.
/// </summary>
public class TreasureItemSettings
{
    /// <summary>Flea base price in roubles (also the handbook price).</summary>
    [JsonPropertyName("price")]
    public int Price { get; set; }

    /// <summary>How many locks it opens before it breaks. Ignored while <see cref="Unlimited"/> is on.</summary>
    [JsonPropertyName("uses")]
    public int Uses { get; set; }

    /// <summary>Never breaks.</summary>
    [JsonPropertyName("unlimited")]
    public bool Unlimited { get; set; }

    /// <summary>Percent chance, per raid, that one copy lies loose somewhere on the map.</summary>
    [JsonPropertyName("looseChance")]
    public double LooseChance { get; set; }

    /// <summary>Percent chance, per raid, that one copy sits in some container (marked rooms included).</summary>
    [JsonPropertyName("containerChance")]
    public double ContainerChance { get; set; }

    /// <summary>Percent chance, per generated bot, that it carries one.</summary>
    [JsonPropertyName("botChance")]
    public double BotChance { get; set; }

    /// <summary>The template's <c>MaximumNumberOfUsage</c>: 0 is how the game spells "unlimited".</summary>
    [JsonIgnore]
    public int MaximumNumberOfUsage => Unlimited ? 0 : Uses;

    public TreasureItemSettings Normalised() => new()
    {
        Price = TreasureLimits.ClampPrice(Price),
        Uses = TreasureLimits.ClampUses(Uses),
        Unlimited = Unlimited,
        LooseChance = TreasureLimits.ClampChance(LooseChance),
        ContainerChance = TreasureLimits.ClampChance(ContainerChance),
        BotChance = TreasureLimits.ClampChance(BotChance),
    };

    public static TreasureItemSettings DefaultKeycard() => new()
    {
        Price = TreasureLimits.KeycardDefaultPrice,
        Uses = TreasureLimits.KeycardDefaultUses,
    };

    public static TreasureItemSettings DefaultKey() => new()
    {
        Price = TreasureLimits.KeyDefaultPrice,
        Uses = TreasureLimits.KeyDefaultUses,
    };

    public static TreasureItemSettings DefaultMarkedKey() => new()
    {
        Price = TreasureLimits.MarkedKeyDefaultPrice,
        Uses = TreasureLimits.MarkedKeyDefaultUses,
    };

    public static TreasureItemSettings DefaultLabsPass() => new()
    {
        Price = TreasureLimits.LabsPassDefaultPrice,
        Uses = TreasureLimits.LabsPassDefaultUses,
    };
}

/// <summary>The whole config: one block per item. Doubles as the <c>/set</c> request body.</summary>
public record TreasureSettings : IRequestData
{
    [JsonPropertyName("keycard")]
    public TreasureItemSettings? Keycard { get; set; }

    [JsonPropertyName("key")]
    public TreasureItemSettings? Key { get; set; }

    [JsonPropertyName("markedKey")]
    public TreasureItemSettings? MarkedKey { get; set; }

    [JsonPropertyName("labsPass")]
    public TreasureItemSettings? LabsPass { get; set; }

    /// <summary>
    /// Fills a missing block with defaults and clamps every number into range. An older config
    /// without a <c>markedKey</c> (1.0.0) or <c>labsPass</c> (1.1.0) block simply gets the defaults.
    /// </summary>
    public TreasureSettings Normalised() => new()
    {
        Keycard = (Keycard ?? TreasureItemSettings.DefaultKeycard()).Normalised(),
        Key = (Key ?? TreasureItemSettings.DefaultKey()).Normalised(),
        MarkedKey = (MarkedKey ?? TreasureItemSettings.DefaultMarkedKey()).Normalised(),
        LabsPass = (LabsPass ?? TreasureItemSettings.DefaultLabsPass()).Normalised(),
    };

    public static TreasureSettings Defaults() => new TreasureSettings().Normalised();
}
