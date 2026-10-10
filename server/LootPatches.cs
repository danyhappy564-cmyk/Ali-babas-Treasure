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

/// <summary>The treasures and their settings blocks, plus the per-bot roll.</summary>
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
}

/// <summary>
/// Reports, once a map's loot is generated, which treasures the game's own draw actually placed
/// (they come from the weighted loot tables <see cref="TreasurePools"/> feeds). Read-only: it
/// never changes the loot.
/// </summary>
public class LocationLootPatch() : AbstractPatch("AlibabaTreasure.LocationLootReport")
{
    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(LocationLootGenerator), nameof(LocationLootGenerator.GenerateLocationLoot));

    [PatchPostfix]
    public static void Postfix(string locationId, List<SpawnpointTemplate> __result)
    {
        try
        {
            if (__result is null)
            {
                return;
            }

            var found = new List<string>();
            foreach (var point in __result)
            {
                foreach (var item in point.Items ?? [])
                {
                    var template = item.Template.ToString();
                    if (TreasureIds.IsTreasure(template))
                    {
                        found.Add($"{template} ({(point.IsContainer == true ? "container" : "loose")})");
                    }
                }
            }

            var logger = TreasureRuntime.Instance?.Logger;
            if (found.Count > 0)
            {
                logger?.Info($"{TreasureMod.LogPrefix} {locationId}: this raid's loot holds {string.Join(", ", found)}");
            }
            else if (TreasureConfigStore.Current.Debug)
            {
                logger?.Info($"{TreasureMod.LogPrefix} [debug] {locationId}: no treasure drawn into this raid's loot");
            }
        }
        catch (Exception ex)
        {
            TreasureRuntime.Instance?.Logger.Warning($"{TreasureMod.LogPrefix} loot report failed on {locationId}: {ex.Message}");
        }
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
