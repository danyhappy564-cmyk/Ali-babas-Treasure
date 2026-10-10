using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Generators.Ragfair;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Ragfair;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Ragfair;

namespace AlibabaTreasure.Server;

/// <summary>
/// The live side of the mod: pushes a settings change into the item templates and the flea, and
/// gives the static Harmony patches the services they cannot get injected.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class TreasureRuntime(
    ISptLogger<TreasureRuntime> logger,
    TemplateTable templates,
    LocaleTable locales,
    BotGeneratorHelper botGeneratorHelper,
    RagfairOfferService ragfairOfferService,
    RagfairOfferGenerator ragfairOfferGenerator
)
{
    private bool _patched;
    private List<string>? _markedKeys;

    // Read by the static patches. Set once in EnablePatches, before either patch can run.
    internal static TreasureRuntime? Instance { get; private set; }
    internal TemplateTable Templates => templates;
    internal BotGeneratorHelper BotGeneratorHelper => botGeneratorHelper;
    internal ISptLogger<TreasureRuntime> Logger => logger;

    internal void EnablePatches()
    {
        Instance = this;
        if (_patched)
        {
            return;
        }

        _patched = true;
        new LocationLootPatch().Enable();
        new BotLootPatch().Enable();
    }

    /// <summary>
    /// Applies <paramref name="applied"/> to everything that caches a number from it. Spawn chances
    /// need nothing here - the patches read <see cref="TreasureConfigStore.Current"/> per raid / bot.
    /// </summary>
    internal List<string> Apply(TreasureSettings previous, TreasureSettings applied)
    {
        var changes = new List<string>();
        ApplyItem(TreasureIds.KeycardId, "keycard", previous.Keycard!, applied.Keycard!, changes);
        ApplyItem(TreasureIds.KeyId, "key", previous.Key!, applied.Key!, changes);
        ApplyItem(TreasureIds.MarkedKeyId, "marked key", previous.MarkedKey!, applied.MarkedKey!, changes);
        ApplyItem(TreasureIds.LabsPassId, "labs pass", previous.LabsPass!, applied.LabsPass!, changes);
        return changes;
    }

    /// <summary>
    /// Every key the marked-room key stands in for: the vanilla marked keys plus any mechanical key
    /// a mod adds with "marked" in its English name. Built on the first request from the client,
    /// when every mod (WTT-Backport and friends) has long since registered its items and locales.
    /// </summary>
    internal List<string> MarkedKeyIds()
    {
        if (_markedKeys is not null)
        {
            return _markedKeys;
        }

        var result = new HashSet<string>(TreasureIds.VanillaMarkedKeys);
        try
        {
            var keyParent = new MongoId(TreasureIds.KeyParentId);
            var english = locales.Global.TryGetValue("en", out var lazy) ? lazy.Value : null;
            if (english is not null)
            {
                foreach (var (id, template) in templates.Items)
                {
                    var text = id.ToString();
                    if (template.Parent == keyParent
                        && !TreasureIds.IsTreasure(text)
                        && english.TryGetValue($"{text} Name", out var name)
                        && name?.Contains("marked", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        result.Add(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"{TreasureMod.LogPrefix} could not scan for modded marked keys ({ex.Message}); using the vanilla list.");
        }

        _markedKeys = result.OrderBy(id => id).ToList();
        logger.Info($"{TreasureMod.LogPrefix} marked-room key covers {_markedKeys.Count} key(s).");
        return _markedKeys;
    }

    private void ApplyItem(string id, string label, TreasureItemSettings before, TreasureItemSettings after, List<string> changes)
    {
        var itemId = new MongoId(id);
        if (!templates.Items.TryGetValue(itemId, out var template) || template.Properties is null)
        {
            return;
        }

        if (before.MaximumNumberOfUsage != after.MaximumNumberOfUsage)
        {
            template.Properties.MaximumNumberOfUsage = after.MaximumNumberOfUsage;
            changes.Add($"{label} uses {(after.Unlimited ? "unlimited" : after.Uses.ToString())}");
        }

        if (before.Price != after.Price)
        {
            templates.Prices[itemId] = after.Price;
            var handbook = templates.Handbook.Items.FirstOrDefault(entry => entry.Id == itemId);
            if (handbook is not null)
            {
                handbook.Price = after.Price;
            }

            RefreshFleaOffers(itemId);
            changes.Add($"{label} price {after.Price:N0}");
        }
    }

    /// <summary>
    /// Flea offers carry the price they were generated with, so a price change only shows once they
    /// expire. Instead, the generated (non-player) offers for the item are pulled and the same number
    /// regenerated at once through the flea's own expired-offer path, which prices them from the
    /// table just updated.
    /// </summary>
    private void RefreshFleaOffers(MongoId itemId)
    {
        try
        {
            var stale = (ragfairOfferService.GetOffersOfType(itemId) ?? [])
                .Where(offer => offer.CreatedBy != OfferCreator.Player)
                .ToList();

            foreach (var offer in stale)
            {
                ragfairOfferService.RemoveOfferById(offer.Id);
            }

            var count = Math.Max(stale.Count, 1);
            var replacements = Enumerable.Range(0, count)
                .Select(_ => new List<Item>
                {
                    new()
                    {
                        Id = new MongoId(),
                        Template = itemId,
                        ParentId = "hideout",
                        SlotId = "hideout",
                        Upd = new Upd { StackObjectsCount = 1 },
                    },
                })
                .ToList();

            ragfairOfferGenerator.GenerateDynamicOffers(replacements);
        }
        catch (Exception ex)
        {
            // Not fatal: the new price still applies as the current offers expire and regenerate.
            logger.Warning($"{TreasureMod.LogPrefix} could not refresh flea offers for {itemId}: {ex.Message}");
        }
    }
}
