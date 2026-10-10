using System;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace AlibabaTreasure.Client;

/// <summary>
/// Rewrites the use-count lines of a treasure's tooltip / inspect window.
/// </summary>
/// <remarks>
/// <para>
/// <c>KeyComponent</c>'s constructor (runs when an item object is created) adds two attributes:
/// the key type ("unlimited / reusable / single use") and the remaining uses. It captures
/// <c>MaximumNumberOfUsage</c> at that moment, and only adds the uses line when it is above 0 - so
/// after an F12 change an existing copy kept showing the old state until the game restarted.
/// </para>
/// <para>
/// For the treasures both lines are replaced with ones that read the template on every draw, so
/// an F12 change shows at once. The passes label the count as entries ("남은 입장 횟수 3/10회").
/// </para>
/// </remarks>
[HarmonyPatch(typeof(KeyComponent), MethodType.Constructor, new[] { typeof(Item), typeof(IKeyComponentTemplate) })]
internal static class TooltipPatch
{
    [HarmonyPostfix]
    private static void Postfix(KeyComponent __instance, Item item, IKeyComponentTemplate template)
    {
        try
        {
            if (item?.Attributes is null || template is null || !TreasureIds.IsTreasure(template.KeyId))
            {
                return;
            }

            var isPass = TreasureIds.IsPass(template.KeyId);
            item.Attributes.RemoveAll(attribute =>
                attribute.Id is EItemAttributeId id && (id == EItemAttributeId.Keys || id == EItemAttributeId.KeyUses));

            item.Attributes.Add(new ItemAttribute(EItemAttributeId.Keys)
            {
                Name = EItemAttributeId.Keys.GetName(),
                StringValue = () => __instance.KeycardType(template.MaximumNumberOfUsage),
                DisplayType = () => EItemAttributeDisplayType.Compact,
            });

            item.Attributes.Add(new ItemAttribute(EItemAttributeId.KeyUses)
            {
                Name = EItemAttributeId.KeyUses.GetName(),
                DisplayNameFunc = isPass ? () => Loc.L("남은 입장 횟수", "Entries left") : null,
                StringValue = () => UsesText(__instance, template, isPass),
                DisplayType = () => EItemAttributeDisplayType.Compact,
            });
        }
        catch (Exception ex)
        {
            TreasurePlugin.Log?.LogWarning($"Tooltip rewrite failed: {ex.Message}");
        }
    }

    private static string UsesText(KeyComponent key, IKeyComponentTemplate template, bool isPass)
    {
        var maximum = template.MaximumNumberOfUsage;
        if (maximum <= 0)
        {
            return string.Format(KeyComponent.CyanColorFormat, "keycard_unlimited".Localized());
        }

        var left = Math.Max(0, maximum - key.NumberOfUsages);
        var count = maximum > 1 && left == 1 ? string.Format(KeyComponent.RedColorFormat, left) : left.ToString();
        return isPass ? Loc.L($"{count}/{maximum}회", $"{count}/{maximum}") : $"{count}/{maximum}";
    }
}
