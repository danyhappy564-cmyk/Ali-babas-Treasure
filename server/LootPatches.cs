using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;

namespace AlibabaTreasure.Server;

/// <summary>Shared bits of the two spawn patches.</summary>
internal static class TreasureSpawn
{
    internal static readonly (string Id, Func<TreasureSettings, TreasureItemSettings> Settings)[] Items =
    [
        (TreasureIds.KeycardId, s => s.Keycard!),
        (TreasureIds.KeyId, s => s.Key!),
        (TreasureIds.MarkedKeyId, s => s.MarkedKey!),
        (TreasureIds.LabsPassId, s => s.LabsPass!),
        (TreasureIds.LabyrinthPassId, s => s.LabyrinthPass!),
    ];

    /// <summary>True with <paramref name="percent"/>% probability. 0 never rolls at all.</summary>
    internal static bool Roll(double percent) => percent > 0 && Random.Shared.NextDouble() * 100.0 < percent;

    /// <summary>
    /// An item that can be swapped for a treasure in place without touching the container grid:
    /// it takes exactly one cell (the treasures are 1x1), has no attachments, and is not a quest item.
    /// </summary>
    internal static bool IsSwappable(MongoId template)
    {
        var runtime = TreasureRuntime.Instance;
        if (runtime is null || TreasureIds.IsTreasure(template.ToString()))
        {
            return false;
        }

        if (!runtime.Templates.Items.TryGetValue(template, out var item) || item.Properties is null)
        {
            return false;
        }

        var props = item.Properties;
        return props.Width == 1 && props.Height == 1 && props.QuestItem != true;
    }
}

/// <summary>
/// Rolls the per-raid loose-loot and container chances once a map's loot has been generated.
/// </summary>
/// <remarks>
/// A hit swaps one random eligible item for the treasure rather than adding a new one: the
/// replaced item already owns a valid cell in its container (or a valid spot on the floor), so
/// there is no grid to re-pack and nothing can overlap. The spawn point and item are replaced with
/// copies, never edited in place - some of them (static weapons, forced spawns) are the database's
/// own objects.
/// </remarks>
public class LocationLootPatch() : AbstractPatch("AlibabaTreasure.LocationLoot")
{
    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(LocationLootGenerator), nameof(LocationLootGenerator.GenerateLocationLoot));

    [PatchPostfix]
    public static void Postfix(string locationId, List<SpawnpointTemplate> __result)
    {
        try
        {
            if (__result is null || __result.Count == 0)
            {
                return;
            }

            var settings = TreasureConfigStore.Current;
            foreach (var (id, select) in TreasureSpawn.Items)
            {
                var item = select(settings);
                var template = new MongoId(id);
                Roll(settings.Debug, locationId, id, "loose", item.LooseChance, () => PlaceLoose(__result, template));
                Roll(settings.Debug, locationId, id, "container", item.ContainerChance, () => PlaceInContainer(__result, template));
            }
        }
        catch (Exception ex)
        {
            TreasureRuntime.Instance?.Logger.Warning($"{TreasureMod.LogPrefix} loot roll failed on {locationId}: {ex.Message}");
        }
    }

    private static void Log(string message) => TreasureRuntime.Instance?.Logger.Info($"{TreasureMod.LogPrefix} {message}");

    /// <summary>One chance roll. A hit is always logged; with debug on, misses and "no free spot" too.</summary>
    private static void Roll(bool debug, string locationId, string id, string kind, double chance, Func<bool> place)
    {
        if (chance <= 0)
        {
            return;
        }

        if (!TreasureSpawn.Roll(chance))
        {
            if (debug)
            {
                Log($"[debug] {locationId}: {id} {kind} {chance}% -> miss");
            }

            return;
        }

        if (place())
        {
            Log($"{id} placed as {kind} loot on {locationId} ({chance}%)");
        }
        else
        {
            Log($"[debug] {locationId}: {id} {kind} {chance}% -> hit, but no swappable 1x1 item found");
        }
    }

    private static bool PlaceLoose(List<SpawnpointTemplate> loot, MongoId template)
    {
        var candidates = new List<int>();
        for (var i = 0; i < loot.Count; i++)
        {
            var point = loot[i];
            if (point.IsContainer == true || point.IsAlwaysSpawn == true || point.Items is null)
            {
                continue;
            }

            var items = point.Items as IList<SptLootItem> ?? point.Items.ToList();
            if (items.Count == 1 && TreasureSpawn.IsSwappable(items[0].Template))
            {
                candidates.Add(i);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var index = candidates[Random.Shared.Next(candidates.Count)];
        var chosen = loot[index];
        var original = chosen.Items!.First();
        loot[index] = chosen with { Items = [original with { Template = template, Upd = null }] };
        return true;
    }

    private static bool PlaceInContainer(List<SpawnpointTemplate> loot, MongoId template)
    {
        // (spawn point index, item index inside it)
        var candidates = new List<(int Point, int Item)>();
        for (var i = 0; i < loot.Count; i++)
        {
            var point = loot[i];
            if (point.IsContainer != true || point.Items is null)
            {
                continue;
            }

            var items = point.Items.ToList();
            if (items.Count < 2)
            {
                continue;
            }

            var rootId = items[0].Id.ToString();
            var parents = new HashSet<string>(items.Select(item => item.ParentId ?? string.Empty));
            for (var j = 1; j < items.Count; j++)
            {
                var child = items[j];
                if (child.ParentId == rootId
                    && !parents.Contains(child.Id.ToString())
                    && TreasureSpawn.IsSwappable(child.Template))
                {
                    candidates.Add((i, j));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var (pointIndex, itemIndex) = candidates[Random.Shared.Next(candidates.Count)];
        var container = loot[pointIndex];
        var replaced = container.Items!.ToList();
        replaced[itemIndex] = replaced[itemIndex] with { Template = template, Upd = null };
        loot[pointIndex] = container with { Items = replaced };
        return true;
    }
}

/// <summary>Rolls the per-bot chance, while the bot's container map is still being tracked.</summary>
/// <remarks>
/// Hooked on <see cref="BotLootGenerator.GenerateLoot"/> rather than on the finished bot because
/// the inventory generator clears the bot's container-space cache right after this call; adding an
/// item later would place it without knowing which cells are taken.
/// </remarks>
public class BotLootPatch() : AbstractPatch("AlibabaTreasure.BotLoot")
{
    private static readonly HashSet<EquipmentSlots> Slots =
    [
        EquipmentSlots.Pockets,
        EquipmentSlots.TacticalVest,
        EquipmentSlots.Backpack,
    ];

    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(BotLootGenerator), nameof(BotLootGenerator.GenerateLoot));

    [PatchPostfix]
    public static void Postfix(MongoId botId, BotGenerationDetails botGenerationDetails, BotBaseInventory botInventory)
    {
        try
        {
            var runtime = TreasureRuntime.Instance;
            if (runtime is null || botInventory is null || botGenerationDetails.IsPlayerScav)
            {
                return;
            }

            var settings = TreasureConfigStore.Current;
            foreach (var (id, select) in TreasureSpawn.Items)
            {
                if (!TreasureSpawn.Roll(select(settings).BotChance))
                {
                    continue;
                }

                var template = new MongoId(id);
                var itemId = new MongoId();
                var result = runtime.BotGeneratorHelper.AddItemWithChildrenToEquipmentSlot(
                    botId,
                    Slots,
                    itemId,
                    template,
                    [new Item { Id = itemId, Template = template }],
                    botInventory);

                if (result == ItemAddedResult.SUCCESS)
                {
                    runtime.Logger.Info($"{TreasureMod.LogPrefix} {id} given to a {botGenerationDetails.Role} bot");
                }
                else if (settings.Debug)
                {
                    runtime.Logger.Info($"{TreasureMod.LogPrefix} [debug] {id} rolled for a {botGenerationDetails.Role} bot but not added: {result}");
                }
            }
        }
        catch (Exception ex)
        {
            TreasureRuntime.Instance?.Logger.Warning($"{TreasureMod.LogPrefix} bot roll failed: {ex.Message}");
        }
    }
}
