using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;

namespace AlibabaTreasure.Client;

/// <summary>Decides which locks and requirements a treasure stands in for.</summary>
internal static class TreasureLocks
{
    private static readonly HashSet<string> MarkedKeys = new(TreasureIds.VanillaMarkedKeys);

    /// <summary>Adds the marked keys the server found (modded keys named "marked" included).</summary>
    public static void AddMarkedKeys(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            MarkedKeys.Add(id);
        }
    }

    /// <summary>
    /// Which treasure stands in for the key with template <paramref name="keyTemplateId"/>:
    /// keycards → the keycard, marked keys → the marked-room key, any other mechanical key → the key.
    /// Null when the template is not a key at all (an exfil asking for a red rebel, say), or when it
    /// cannot be looked up and <paramref name="unknownIsKey"/> is false.
    /// </summary>
    public static string? TreasureFor(string? keyTemplateId, bool unknownIsKey)
    {
        if (string.IsNullOrEmpty(keyTemplateId) || TreasureIds.IsTreasure(keyTemplateId))
        {
            return null;
        }

        if (MarkedKeys.Contains(keyTemplateId!))
        {
            return TreasureIds.MarkedKeyId;
        }

        try
        {
            if (Singleton<ItemFactory>.Instantiated
                && Singleton<ItemFactory>.Instance.ItemTemplates.TryGetValue(keyTemplateId, out var template))
            {
                return template switch
                {
                    KeycardTemplate => TreasureIds.KeycardId,
                    KeyTemplate => TreasureIds.KeyId,
                    _ => null,
                };
            }
        }
        catch (Exception)
        {
            // fall through to the unknown case
        }

        return unknownIsKey ? TreasureIds.KeyId : null;
    }

    /// <summary>
    /// Card readers (<see cref="KeycardDoor"/>) take the keycard. Any other lock takes whichever
    /// treasure stands in for its key; a lock whose key template is unknown is treated as an
    /// ordinary mechanical lock.
    /// </summary>
    public static bool Opens(string treasureId, WorldInteractiveObject? lockObject)
    {
        if (lockObject is null || string.IsNullOrEmpty(lockObject.KeyId))
        {
            return false;
        }

        var wanted = lockObject is KeycardDoor
            ? TreasureIds.KeycardId
            : TreasureFor(lockObject.KeyId, unknownIsKey: true);
        return treasureId == wanted;
    }
}

/// <summary>
/// The "Unlock" entry on a locked door, trunk, crate or switch only appears when
/// <c>PlayerOwner.GetKey</c> (finds the matching key in your gear) returns one. When it finds no
/// real key, this hands back a treasure that opens the lock instead - a real key is always
/// preferred, so the treasure's uses are only spent when nothing else would work.
/// </summary>
[HarmonyPatch(typeof(PlayerOwner), nameof(PlayerOwner.GetKey))]
internal static class GetKeyPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerOwner __instance, WorldInteractiveObject worldInteractiveObject, ref KeyComponent __result)
    {
        if (__result != null || worldInteractiveObject is null || string.IsNullOrEmpty(worldInteractiveObject.KeyId))
        {
            return;
        }

        try
        {
            var equipment = __instance.Player?.InventoryController?.Inventory?.Equipment;
            if (equipment is null)
            {
                return;
            }

            __result = equipment.GetItemComponentsInChildren<KeyComponent>(onlyMerged: false)
                .FirstOrDefault(key => key.Template != null
                    && TreasureIds.IsTreasure(key.Template.KeyId)
                    && TreasureLocks.Opens(key.Template.KeyId, worldInteractiveObject));
        }
        catch (Exception ex)
        {
            TreasurePlugin.Log?.LogWarning($"GetKey lookup failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Makes the lock accept a treasure. <c>UnlockOperation</c> (checks the key, counts a use and
/// discards a spent key) compares <c>key.Template.KeyId</c> with the lock's <c>KeyId</c>; for a
/// treasure that opens this lock, the lock's <c>KeyId</c> is pointed at the treasure for the length
/// of the call and put back in the finalizer, which runs even if the call throws. The game's own
/// code therefore still does the use counting and the discard at the last use - nothing of it is
/// reimplemented here. Nothing else in the game reads a lock's <c>KeyId</c> (checked against the
/// 4.1 client assembly), so the swap is invisible outside the call.
/// </summary>
internal static class UnlockSwap
{
    public static void Prefix(WorldInteractiveObject __instance, KeyComponent key, ref string? __state)
    {
        __state = null;
        var treasureId = key?.Template?.KeyId;
        if (!TreasureIds.IsTreasure(treasureId) || __instance.KeyId == treasureId)
        {
            return;
        }

        if (TreasureLocks.Opens(treasureId!, __instance))
        {
            __state = __instance.KeyId;
            __instance.KeyId = treasureId;
        }
    }

    public static void Finalizer(WorldInteractiveObject __instance, string? __state)
    {
        if (__state != null)
        {
            __instance.KeyId = __state;
        }
    }
}

/// <summary>Plain locks: doors, trunks, lootable containers, switches.</summary>
[HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.UnlockOperation))]
internal static class WorldInteractiveObjectUnlockPatch
{
    [HarmonyPrefix]
    private static void Prefix(WorldInteractiveObject __instance, KeyComponent key, ref string? __state) =>
        UnlockSwap.Prefix(__instance, key, ref __state);

    [HarmonyFinalizer]
    private static void Finalizer(WorldInteractiveObject __instance, string? __state) =>
        UnlockSwap.Finalizer(__instance, __state);
}

/// <summary>
/// Card readers. <c>KeycardDoor</c> overrides <c>UnlockOperation</c> without calling the base, so
/// it needs its own patch. The reader's "Try keycard" menu already lists every keycard you carry,
/// the treasure keycard included.
/// </summary>
[HarmonyPatch(typeof(KeycardDoor), nameof(KeycardDoor.UnlockOperation))]
internal static class KeycardDoorUnlockPatch
{
    [HarmonyPrefix]
    private static void Prefix(WorldInteractiveObject __instance, KeyComponent key, ref string? __state) =>
        UnlockSwap.Prefix(__instance, key, ref __state);

    [HarmonyFinalizer]
    private static void Finalizer(WorldInteractiveObject __instance, string? __state) =>
        UnlockSwap.Finalizer(__instance, __state);
}

/// <summary>
/// Extracts that need a key or keycard on you. <c>HasItemRequirement.Met</c> (is the required
/// item anywhere in your inventory) also passes when you carry the treasure that stands in for
/// it. Nothing is spent - the vanilla requirement does not spend the real key either.
/// </summary>
[HarmonyPatch(typeof(HasItemRequirement), nameof(HasItemRequirement.Met))]
internal static class ExfilHasItemPatch
{
    [HarmonyPostfix]
    private static void Postfix(HasItemRequirement __instance, Player player, ref bool __result)
    {
        if (__result || player?.Profile?.Inventory is null)
        {
            return;
        }

        try
        {
            var treasure = TreasureLocks.TreasureFor(__instance.Id, unknownIsKey: false);
            if (treasure != null && player.Profile.Inventory.GetAllItemByTemplate(treasure).Any())
            {
                __result = true;
            }
        }
        catch (Exception ex)
        {
            TreasurePlugin.Log?.LogWarning($"Exfil requirement check failed: {ex.Message}");
        }
    }
}

/// <summary>
/// While you stand in an extract zone, the game re-checks its item requirement only when an item
/// that "affects" it is added or removed. Treat the stand-in treasure as affecting it too, so
/// picking one up (or dropping it) inside the zone updates the extract like the real key would.
/// </summary>
internal static class ExfilItemEvent
{
    public static void Check(InventoryRequirements requirement, Item? item, ref bool result)
    {
        var template = item?.StringTemplateId;
        if (!result && TreasureIds.IsTreasure(template)
            && TreasureLocks.TreasureFor(requirement.Id, unknownIsKey: false) == template)
        {
            result = true;
        }
    }
}

[HarmonyPatch(typeof(InventoryRequirements), nameof(InventoryRequirements.AddOperationAffectRequirement))]
internal static class ExfilItemAddedPatch
{
    [HarmonyPostfix]
    private static void Postfix(InventoryRequirements __instance, AddItemEventArgs args, ref bool __result) =>
        ExfilItemEvent.Check(__instance, args?.Item, ref __result);
}

[HarmonyPatch(typeof(InventoryRequirements), nameof(InventoryRequirements.RemoveOperationAffectRequirement))]
internal static class ExfilItemRemovedPatch
{
    [HarmonyPostfix]
    private static void Postfix(InventoryRequirements __instance, RemoveItemEventArgs args, ref bool __result) =>
        ExfilItemEvent.Check(__instance, args?.Item, ref __result);
}
