namespace AlibabaTreasure;

/// <summary>
/// Ids, routes and setting ranges both halves of the mod must agree on. Compiled into the server
/// mod and the client plugin alike, so the item the server registers and the item the plugin
/// recognises at a door can never drift apart.
/// </summary>
internal static class TreasureIds
{
    /// <summary>알리바바의 키카드 — opens every keycard lock.</summary>
    public const string KeycardId = "6ac9e816b4dcb655250d58d7";

    /// <summary>알리바바의 열쇠 — opens every mechanical lock except marked rooms.</summary>
    public const string KeyId = "6ac9e816d3ccc02f796d0218";

    /// <summary>알리바바의 마크방 열쇠 — opens every marked-room lock.</summary>
    public const string MarkedKeyId = "6ac9e816c335dcd430ce50e6";

    /// <summary>
    /// Object #11SR keycard (Interchange). Cloned for its card model, which is already in the
    /// client — nothing to ship.
    /// </summary>
    public const string KeycardCloneSource = "5e42c81886f7742a01529f57";

    /// <summary>Kiba Arms outer door key. Cloned for its plain-key model.</summary>
    public const string KeyCloneSource = "5ad5d7d286f77450166e0a89";

    /// <summary>Dorm room 314 marked key. Cloned for its marked-key model.</summary>
    public const string MarkedKeyCloneSource = "5780cf7f2459777de4559322";

    /// <summary>
    /// The vanilla marked keys (4.1.6 database: every mechanical key whose English name contains
    /// "marked"). The server adds any modded key named the same way at run time; this list is the
    /// fallback the client uses until the server has answered.
    /// </summary>
    public static readonly string[] VanillaMarkedKeys =
    {
        "5780cf7f2459777de4559322", // Dorm room 314 marked key
        "5d80c60f86f77440373c4ece", // RB-BK marked key
        "5d80c62a86f7744036212b3f", // RB-VO marked key
        "5ede7a8229445733cb4c18e2", // RB-PKPM marked key
        "62987dfc402c7f69bf010923", // Shared bedroom marked key (Lighthouse)
        "63a3a93f8a56922e82001f5d", // Abandoned factory marked key
        "64ccc25f95763a1ae376e447", // Mysterious room marked key
    };

    public const string KeycardParentId = "5c164d2286f774194c5e69fa";
    public const string KeyParentId = "5c99f98d86f7745c314214b3";

    public const string HandbookElectronicKeys = "5c518ed586f774119a772aee";
    public const string HandbookMechanicalKeys = "5c518ec986f7743b68682ce2";

    public const string RouteGet = "/alibabatreasure/settings/get";
    public const string RouteSet = "/alibabatreasure/settings/set";

    public static bool IsTreasure(string? templateId) =>
        templateId == KeycardId || templateId == KeyId || templateId == MarkedKeyId;
}

/// <summary>
/// Setting ranges and defaults. The F12 sliders and the server's clamp read the same numbers, so
/// a value the server accepts is always one the slider can show (kb 10.6).
/// </summary>
internal static class TreasureLimits
{
    public const int PriceMin = 10_000;
    public const int PriceMax = 50_000_000;

    public const int UsesMin = 1;
    public const int UsesMax = 100;

    public const double ChanceMin = 0;
    public const double ChanceMax = 100;

    public const int KeycardDefaultPrice = 5_000_000;
    public const int KeyDefaultPrice = 3_000_000;
    public const int MarkedKeyDefaultPrice = 8_000_000;

    public const int KeycardDefaultUses = 10;
    public const int KeyDefaultUses = 10;
    public const int MarkedKeyDefaultUses = 10;

    public static int ClampPrice(int value) => value < PriceMin ? PriceMin : value > PriceMax ? PriceMax : value;

    public static int ClampUses(int value) => value < UsesMin ? UsesMin : value > UsesMax ? UsesMax : value;

    /// <summary>Clamps to 0–100 and rounds to the 0.1 step the F12 slider offers.</summary>
    public static double ClampChance(double value)
    {
        if (double.IsNaN(value) || value < ChanceMin)
        {
            return ChanceMin;
        }

        if (value > ChanceMax)
        {
            return ChanceMax;
        }

        return System.Math.Round(value, 1);
    }
}
