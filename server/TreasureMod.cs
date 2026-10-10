using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Items;

namespace AlibabaTreasure.Server;

/// <summary>
/// Registers 알리바바의 키카드 and 알리바바의 열쇠, and keeps them out of every place the game could
/// hand one out for free.
/// </summary>
/// <remarks>
/// <para>
/// Both items are clones of vanilla templates (a Labs keycard and a marked key), so their models are
/// already in the client and there is no bundle to ship (same approach as SJ-0 Nocturne).
/// </para>
/// <para>
/// Runs at <see cref="OnLoadOrder.Preload"/>: SPT 4.1's database integrity check snapshots the item
/// table once profiles load and kills the server if anything appears after that (kb 1.4). It is
/// also before the fence assort, the flea and every loot cache are built, so the blacklists below
/// are in place before anything reads them.
/// </para>
/// </remarks>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 60)]
public class TreasureMod(
    ISptLogger<TreasureMod> logger,
    TreasureConfigStore configStore,
    TreasureRuntime runtime,
    TemplateTable templates,
    LocaleTable locales,
    ItemConfig itemConfig,
    ItemFilterService itemFilterService
) : IOnLoad
{
    internal const string LogPrefix = "[AlibabaTreasure]";

    private sealed record ItemDefinition(
        string Id,
        string CloneSource,
        string Name,
        string NameKo,
        string ShortName,
        string ShortNameKo,
        string Description,
        string DescriptionKo,
        string HandbookCategory,
        Func<TreasureSettings, TreasureItemSettings> Settings);

    private static readonly ItemDefinition[] Definitions =
    [
        new(
            TreasureIds.KeycardId,
            TreasureIds.KeycardCloneSource,
            "Ali Baba's Keycard",
            "알리바바의 키카드",
            "Ali Card",
            "알리 카드",
            "A golden access card nobody at TerraGroup admits to issuing. Every card reader it is held "
            + "against blinks green - Labs doors, storage rooms, all of them. Its chip is not built to last.",
            "테라그룹 누구도 발급한 적 없다는 황금빛 출입 카드. 갖다 대는 카드 리더마다 초록불이 켜진다 — "
            + "랩스 문도, 창고도, 전부. 칩은 오래 버티도록 만들어지지 않았다.",
            TreasureIds.HandbookElectronicKeys,
            s => s.Keycard!),
        new(
            TreasureIds.KeyId,
            TreasureIds.KeyCloneSource,
            "Ali Baba's Key",
            "알리바바의 열쇠",
            "Ali Key",
            "알리 열쇠",
            "Open sesame. A worn master key that turns in any mechanical lock in Tarkov - marked rooms "
            + "included. The teeth wear down a little with every door.",
            "열려라, 참깨. 타르코프의 어떤 기계식 자물쇠에도 돌아가는 낡은 마스터키 — 마크방도 예외가 아니다. "
            + "문을 열 때마다 이가 조금씩 닳는다.",
            TreasureIds.HandbookMechanicalKeys,
            s => s.Key!),
    ];

    public Task OnLoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = configStore.Load();
        var registered = new List<MongoId>();

        foreach (var definition in Definitions)
        {
            if (Register(definition, definition.Settings(settings)))
            {
                registered.Add(new MongoId(definition.Id));
            }
        }

        if (registered.Count == 0)
        {
            return Task.CompletedTask;
        }

        Blacklist(registered);
        runtime.EnablePatches();

        logger.Success(
            $"{LogPrefix} registered {registered.Count} item(s). "
            + $"Keycard {Describe(settings.Keycard!)} | Key {Describe(settings.Key!)}. "
            + "Flea market only - spawn chances are 0% unless changed in F12.");
        return Task.CompletedTask;
    }

    private static string Describe(TreasureItemSettings s) =>
        $"{s.Price:N0}₽, {(s.Unlimited ? "unlimited" : $"{s.Uses} uses")}, "
        + $"loose {s.LooseChance}% / container {s.ContainerChance}% / bot {s.BotChance}%";

    private bool Register(ItemDefinition definition, TreasureItemSettings settings)
    {
        var itemId = new MongoId(definition.Id);
        if (templates.Items.ContainsKey(itemId))
        {
            logger.Warning($"{LogPrefix} {definition.Id} is already registered - another mod uses this id. Skipping {definition.Name}.");
            return false;
        }

        if (!templates.Items.TryGetValue(new MongoId(definition.CloneSource), out var source) || source.Properties is null)
        {
            logger.Error($"{LogPrefix} clone source {definition.CloneSource} is missing from the item database - cannot create {definition.Name}.");
            return false;
        }

        // TemplateItem and its properties are records: `with` copies the ~150 fields we keep.
        var props = source.Properties with
        {
            Name = definition.Name,
            ShortName = definition.ShortName,
            Description = definition.Description,
            BackgroundColor = "yellow",

            MaximumNumberOfUsage = settings.MaximumNumberOfUsage,

            CanSellOnRagfair = true,
            // Off: otherwise generated flea barters could ask players to hand one over.
            CanRequireOnRagfair = false,

            // Not_exist keeps it out of every rarity-driven roll (scav case and friends), on top of
            // not being in any loot table and being on the reward/lootable blacklists.
            RarityPvE = "Not_exist",
            SpawnRarity = "Not_exist",

            QuestItem = false,
            ExamineExperience = 0,
            LootExperience = 0,
        };

        templates.Items[itemId] = source with
        {
            Id = itemId,
            Name = definition.Id == TreasureIds.KeycardId ? "alibaba_keycard" : "alibaba_key",
            Properties = props,
        };

        templates.Handbook.Items.RemoveAll(entry => entry.Id == itemId);
        templates.Handbook.Items.Add(new HandbookItem
        {
            Id = itemId,
            ParentId = new MongoId(definition.HandbookCategory),
            Price = settings.Price,
        });

        // The flea prices offers from this table, not from the handbook, so both are set.
        templates.Prices[itemId] = settings.Price;

        RegisterLocales(definition);
        return true;
    }

    /// <summary>
    /// Keeps the items out of everything that builds its pool from the whole item table: PMC
    /// pocket/vest/backpack loot, fence, airdrops, the scav case and bot loot (kb: ItemFilterService
    /// callers). The global <c>Blacklist</c> is deliberately NOT used - the flea assort reads it,
    /// and the flea is the one place these items must appear.
    /// </summary>
    private void Blacklist(List<MongoId> ids)
    {
        foreach (var id in ids)
        {
            itemConfig.RewardItemBlacklist.Add(id);
            itemConfig.LootableItemBlacklist.Add(id);
        }

        // The lootable list is cached when ItemFilterService is constructed; feed the cache too.
        itemFilterService.AddItemToLootableBlacklistCache(ids);
    }

    /// <summary>
    /// Locales are lazy in 4.1: a transformer runs whenever a language is first deserialised, so
    /// nothing is forced to load at boot. Korean gets the Korean text, every other language English.
    /// </summary>
    private void RegisterLocales(ItemDefinition definition)
    {
        var id = definition.Id;
        foreach (var (localeId, lazyLocale) in locales.Global)
        {
            var korean = localeId.StartsWith("kr", StringComparison.OrdinalIgnoreCase)
                || localeId.StartsWith("ko", StringComparison.OrdinalIgnoreCase);

            lazyLocale.AddTransformer(locale =>
            {
                if (locale is null)
                {
                    return locale!;
                }

                locale[$"{id} Name"] = korean ? definition.NameKo : definition.Name;
                locale[$"{id} ShortName"] = korean ? definition.ShortNameKo : definition.ShortName;
                locale[$"{id} Description"] = korean ? definition.DescriptionKo : definition.Description;
                return locale;
            });
        }
    }
}
