using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace AlibabaTreasure.Server;

/// <summary>What both routes answer: the settings now in force, plus what the last change did.</summary>
public record TreasureSettingsResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("saved")]
    public bool Saved { get; init; }

    [JsonPropertyName("changes")]
    public List<string>? Changes { get; init; }

    [JsonPropertyName("keycard")]
    public TreasureItemSettings? Keycard { get; init; }

    [JsonPropertyName("key")]
    public TreasureItemSettings? Key { get; init; }

    [JsonPropertyName("markedKey")]
    public TreasureItemSettings? MarkedKey { get; init; }

    [JsonPropertyName("labsPass")]
    public TreasureItemSettings? LabsPass { get; init; }

    /// <summary>Key templates the marked-room key opens (vanilla + modded "marked" keys).</summary>
    [JsonPropertyName("markedKeys")]
    public List<string>? MarkedKeys { get; init; }
}

/// <summary>
/// The F12 plugin's way in. <c>get</c> returns the live settings; <c>set</c> applies a change at
/// once (prices, uses, flea offers) and saves it to <c>config/config.json</c>. Spawn chances apply
/// from the next raid / next generated bot. Plain JSON both ways - the plugin sends
/// <c>responsecompressed: 0</c> (kb 10.6).
/// </summary>
[Injectable]
public class TreasureRouter(JsonUtil jsonUtil, TreasureConfigStore store, TreasureRuntime runtime) : StaticRouter(jsonUtil,
[
    // A bodiless request arrives as EmptyRequestData; typing it as anything else throws (kb 10.6).
    new RouteAction<EmptyRequestData>(TreasureIds.RouteGet, (_, _, _, _, _) =>
        new ValueTask<string>(Respond(TreasureConfigStore.Current, null, false, runtime.MarkedKeyIds()))),

    new RouteAction<TreasureSettings>(TreasureIds.RouteSet, (_, request, _, _, _) =>
    {
        try
        {
            if (request is null)
            {
                return new ValueTask<string>(Fail("empty request"));
            }

            var (previous, applied, saved) = store.Replace(request);
            var changes = runtime.Apply(previous, applied);
            return new ValueTask<string>(Respond(applied, changes, saved, runtime.MarkedKeyIds()));
        }
        catch (Exception ex)
        {
            return new ValueTask<string>(Fail(ex.Message));
        }
    }),
])
{
    private static string Respond(TreasureSettings settings, List<string>? changes, bool saved, List<string>? markedKeys) =>
        JsonSerializer.Serialize(new TreasureSettingsResponse
        {
            Saved = saved,
            Changes = changes,
            Keycard = settings.Keycard,
            Key = settings.Key,
            MarkedKey = settings.MarkedKey,
            LabsPass = settings.LabsPass,
            MarkedKeys = markedKeys,
        });

    private static string Fail(string error) =>
        JsonSerializer.Serialize(new TreasureSettingsResponse { Ok = false, Error = error });
}
