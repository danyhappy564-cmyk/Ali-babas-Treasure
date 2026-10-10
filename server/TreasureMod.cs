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
/// Registers 알리바바의 키카드, 알리바바의 열쇠 and 알리바바의 마크방 열쇠, and keeps them out of every
/// place the game could hand one out for free.
/// </summary>
/// <remarks>
/// <para>
/// All three are clones of vanilla templates (an Interchange keycard, a plain key, a marked key), so their models are
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
    LocationTable locationTable,
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
            "An access card nobody at TerraGroup admits to issuing. Every card reader it is held against "
            + "blinks green - Labs doors, storage rooms, keycard extracts. It will not get you into the "
            + "Labs themselves. Its chip is not built to last.",
            "테라그룹 누구도 발급한 적 없다는 출입 카드. 갖다 대는 카드 리더마다 초록불이 켜진다 — "
            + "랩스 문도, 창고도, 키카드 탈출구도. 연구소 입장에는 쓸 수 없다. 칩은 오래 버티도록 만들어지지 않았다.",
            TreasureIds.HandbookElectronicKeys,
            s => s.Keycard!),
        new(
            TreasureIds.KeyId,
            TreasureIds.KeyCloneSource,
            "Ali Baba's Key",
            "알리바바의 열쇠",
            "Ali Key",
            "알리 열쇠",
            "Open sesame. A worn master key that turns in any ordinary mechanical lock in Tarkov - doors, "
            + "trunks, crates, key-locked extracts. Marked rooms are the one thing it will not open. "
            + "The teeth wear down a little with every door.",
            "열려라, 참깨. 타르코프의 평범한 기계식 자물쇠라면 어디든 돌아가는 낡은 마스터키 — 문, 트렁크, 상자, "
            + "열쇠가 필요한 탈출구까지. 마크방만은 열리지 않는다. 문을 열 때마다 이가 조금씩 닳는다.",
            TreasureIds.HandbookMechanicalKeys,
            s => s.Key!),
        new(
            TreasureIds.MarkedKeyId,
            TreasureIds.MarkedKeyCloneSource,
            "Ali Baba's Marked Key",
            "알리바바의 마크방 열쇠",
            "Ali Mark",
            "알리 마크",
            "The forty thieves' own key, scratched with a mark nobody can read. It opens every marked "
            + "room in Tarkov and nothing else.",
            "사십인의 도둑이 쓰던 열쇠. 아무도 읽지 못하는 표식이 긁혀 있다. 타르코프의 모든 마크방을 열고, "
            + "그 밖의 문은 하나도 열지 않는다.",
            TreasureIds.HandbookMechanicalKeys,
            s => s.MarkedKey!),
        new(
            TreasureIds.LabsPassId,
            TreasureIds.LabsPassCloneSource,
            "Ali Baba's Labs Pass",
            "알리바바의 연구소 출입증",
            "Ali Lab",
            "알리 연구소",
            "A black TerraGroup pass that the Labs checkpoint never seems to cancel. It lets you into "
            + "The Lab again and again, one entry per use. It opens no doors inside.",
            "연구소 검문소가 한 번도 무효 처리하지 않는 검은 테라그룹 출입증. 쓸 때마다 한 번씩, 연구소에 몇 번이고 "
            + "들어갈 수 있다. 안에 있는 문은 하나도 열지 못한다.",
            TreasureIds.HandbookElectronicKeys,
            s => s.LabsPass!),
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
        var accessMaps = AddRaidAccessKeys();
        runtime.EnablePatches();

        logger.Success(
            $"{LogPrefix} registered {registered.Count} item(s). "
            + $"Keycard {Describe(settings.Keycard!)} | Key {Describe(settings.Key!)} | Marked key {Describe(settings.MarkedKey!)} "
            + $"| Labs pass {Describe(settings.LabsPass!)}. "
            + $"Raid entry accepted on: {(accessMaps.Count == 0 ? "-" : string.Join(", ", accessMaps))}. "
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
            // The raid-entry check only accepts an examined card (MainMenuShowOperation); a flea
            // purchase should work at the Labs entrance without inspecting it first.
            ExaminedByDefault = true,
            ExamineExperience = 0,
            LootExperience = 0,
        };

        templates.Items[itemId] = source with
        {
            Id = itemId,
            Name = definition.Id switch
            {
                TreasureIds.KeycardId => "alibaba_keycard",
                TreasureIds.MarkedKeyId => "alibaba_key_marked",
                TreasureIds.LabsPassId => "alibaba_labs_pass",
                _ => "alibaba_key",
            },
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
    /// Lets the Labs pass stand in for the single-use Labs access keycard on every map that asks for
    /// it (only The Lab in 4.1.6). The other treasures are deliberately not entry keys.
    /// </summary>
    /// <remarks>
    /// The client reads one list for both checks: the main menu refuses the raid unless an equipped,
    /// examined item's template is in <c>AccessKeys</c>, and <c>BaseLocalGame</c> spends a use of that
    /// same item when the raid starts (removing it when the count reaches the maximum, never when
    /// uses are unlimited). Adding the pass's id to the list is therefore all it takes (kb 2.37).
    /// </remarks>
    private List<string> AddRaidAccessKeys()
    {
        var maps = new List<string>();
        foreach (var (name, location) in locationTable.GetDictionary())
        {
            var locationBase = location?.Base;
            if (locationBase is null)
            {
                continue;
            }

            var changed = false;
            locationBase.AccessKeys = Extend(locationBase.AccessKeys, ref changed);
            locationBase.AccessKeysPvE = Extend(locationBase.AccessKeysPvE, ref changed);
            if (changed)
            {
                maps.Add(name);
            }
        }

        return maps;
    }

    private static IEnumerable<string>? Extend(IEnumerable<string>? accessKeys, ref bool changed)
    {
        if (accessKeys is null)
        {
            return null;
        }

        var keys = accessKeys.ToList();
        if (!keys.Contains(TreasureIds.LabsAccessKeycardId) || keys.Contains(TreasureIds.LabsPassId))
        {
            return accessKeys;
        }

        changed = true;
        keys.Add(TreasureIds.LabsPassId);
        return keys.ToArray();
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
