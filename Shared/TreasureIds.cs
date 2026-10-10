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

    /// <summary>알리바바의 열쇠 — opens every mechanical lock (marked rooms included).</summary>
    public const string KeyId = "6ac9e816d3ccc02f796d0218";

    /// <summary>
    /// TerraGroup Labs keycard (Yellow). Cloned for its golden card model, which is already in
    /// the client — nothing to ship.
    /// </summary>
    public const string KeycardCloneSource = "5c1d0d6d86f7744bb2683e1f";

    /// <summary>Dorm room 314 marked key. Cloned for its marked-key model.</summary>
    public const string KeyCloneSource = "5780cf7f2459777de4559322";

    public const string KeycardParentId = "5c164d2286f774194c5e69fa";
    public const string KeyParentId = "5c99f98d86f7745c314214b3";

    public const string HandbookElectronicKeys = "5c518ed586f774119a772aee";
    public const string HandbookMechanicalKeys = "5c518ec986f7743b68682ce2";

    public const string RouteGet = "/alibabatreasure/settings/get";
    public const string RouteSet = "/alibabatreasure/settings/set";

    public static bool IsTreasure(string? templateId) => templateId == KeycardId || templateId == KeyId;
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
    public const int KeyDefaultPrice = 6_000_000;

    public const int KeycardDefaultUses = 10;
    public const int KeyDefaultUses = 10;

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
