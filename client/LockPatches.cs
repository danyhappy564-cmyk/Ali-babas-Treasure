using System;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;

namespace AlibabaTreasure.Client;

/// <summary>Decides which locks a treasure opens.</summary>
internal static class TreasureLocks
{
    /// <summary>
    /// The keycard opens card readers (<see cref="KeycardDoor"/>) and any other lock whose key is a
    /// keycard; the key opens every lock whose key is a mechanical key. A lock whose key template
    /// cannot be looked up is treated as mechanical.
    /// </summary>
    public static bool Opens(string treasureId, WorldInteractiveObject? lockObject)
    {
        if (lockObject is null || string.IsNullOrEmpty(lockObject.KeyId))
        {
            return false;
        }

        var wantsKeycard = lockObject is KeycardDoor || NeedsKeycard(lockObject.KeyId);
        return treasureId == (wantsKeycard ? TreasureIds.KeycardId : TreasureIds.KeyId);
    }

    private static bool NeedsKeycard(string keyId)
    {
        try
        {
            return Singleton<ItemFactory>.Instantiated
                && Singleton<ItemFactory>.Instance.ItemTemplates.TryGetValue(keyId, out var template)
                && template is KeycardTemplate;
        }
        catch (Exception)
        {
            return false;
        }
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
