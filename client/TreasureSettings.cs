using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AlibabaTreasure.Client;

/// <summary>Korean/English switch for every string the F12 window shows.</summary>
internal static class Loc
{
    public const string Auto = "Auto · 자동";
    public const string Korean = "한국어 · Korean";
    public const string English = "English · 영어";

    public static bool En;

    public static string L(string korean, string english) => En ? english : korean;

    public static bool ResolveEnglish(string choice) => choice switch
    {
        Korean => false,
        English => true,
        // Auto: Korean only on a Korean Windows, English for everyone else.
        _ => Application.systemLanguage != SystemLanguage.Korean,
    };
}

/// <summary>One treasure item's six F12 entries.</summary>
internal sealed class ItemEntries
{
    public ConfigEntry<int> Price = null!;
    public ConfigEntry<int> Uses = null!;
    public ConfigEntry<bool> Unlimited = null!;
    public ConfigEntry<float> LooseChance = null!;
    public ConfigEntry<float> ContainerChance = null!;
    public ConfigEntry<float> BotChance = null!;

    public ItemValues Read() => new()
    {
        Price = Price.Value,
        Uses = Uses.Value,
        Unlimited = Unlimited.Value,
        LooseChance = Math.Round(LooseChance.Value, 1),
        ContainerChance = Math.Round(ContainerChance.Value, 1),
        BotChance = Math.Round(BotChance.Value, 1),
    };

    /// <summary>Writes server values in, touching only entries that actually differ (kb 10.6).</summary>
    public void Write(ItemValues values)
    {
        Set(Price, values.Price);
        Set(Uses, values.Uses);
        Set(Unlimited, values.Unlimited);
        Set(LooseChance, (float)values.LooseChance);
        Set(ContainerChance, (float)values.ContainerChance);
        Set(BotChance, (float)values.BotChance);
    }

    private static void Set<T>(ConfigEntry<T> entry, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(entry.Value, value))
        {
            entry.Value = value;
        }
    }

    /// <summary>The <c>MaximumNumberOfUsage</c> the game should see: 0 means unlimited.</summary>
    public int MaximumNumberOfUsage => Unlimited.Value ? 0 : Uses.Value;
}

/// <summary>
/// Every F12 entry. The .cfg sections and keys are fixed English names so the file reads the same
/// in either language; what the window shows comes from <see cref="ConfigurationManagerAttributes"/>
/// that are rewritten whenever the language changes (kb 10.9).
/// </summary>
internal sealed class TreasureSettings
{
    private sealed class Text
    {
        public ConfigurationManagerAttributes Attributes = null!;
        public string CategoryKo = "", CategoryEn = "", NameKo = "", NameEn = "", HelpKo = "", HelpEn = "";
    }

    private readonly List<Text> _texts = new();

    public ConfigEntry<string> Language { get; }
    public ItemEntries Keycard { get; }
    public ItemEntries Key { get; }
    public ItemEntries MarkedKey { get; }
    public ItemEntries LabsPass { get; }

    public TreasureSettings(ConfigFile config)
    {
        Language = config.Bind(
            "00. Language",
            "Language",
            Loc.Auto,
            new ConfigDescription(
                "F12 display language. Auto = Korean on Korean Windows, English otherwise.",
                new AcceptableValueList<string>(Loc.Auto, Loc.Korean, Loc.English),
                Attributes("00. Language · 언어", "00. Language · 언어", "Language · 언어", "Language · 언어",
                    "F12 화면 언어. 자동 = 한국어 윈도우면 한국어, 아니면 영어.\n바꾼 뒤 F12 창을 닫았다 다시 열면 적용됩니다.",
                    "F12 display language. Auto = Korean on Korean Windows, English otherwise.\n"
                    + "Close and reopen the F12 window after changing it.",
                    100)));

        Keycard = BindItem(config, "01. Keycard", "01. 알리바바의 키카드", "01. Ali Baba's Keycard",
            "카드 리더가 달린 모든 문(랩스 색깔 문, 창고 등), 키카드가 필요한 모든 잠금·탈출구 (연구소 입장은 안 됨 → 04번 출입증)",
            "every card-reader door (Labs colour doors, storage rooms, ...), every lock or extract that takes a keycard (not Labs entry - see 04)",
            TreasureLimits.KeycardDefaultPrice, TreasureLimits.KeycardDefaultUses);

        Key = BindItem(config, "02. Key", "02. 알리바바의 열쇠", "02. Ali Baba's Key",
            "마크방을 뺀 모든 기계식 자물쇠(일반 문·차 트렁크·잠긴 상자·스위치)와 열쇠가 필요한 탈출구",
            "every mechanical lock except marked rooms (doors, car trunks, locked crates, switches) and every extract that needs a key",
            TreasureLimits.KeyDefaultPrice, TreasureLimits.KeyDefaultUses);

        MarkedKey = BindItem(config, "03. Marked key", "03. 알리바바의 마크방 열쇠", "03. Ali Baba's Marked Key",
            "모든 마크방(세관 기숙사 314, 리저브 RB-BK/VO/PKPM, 등대, 시내 2곳, 이름에 'marked'가 붙은 모드 열쇠의 방까지). 다른 문은 못 엽니다",
            "every marked room (Customs dorm 314, Reserve RB-BK/VO/PKPM, Lighthouse, two on Streets, and any modded key named 'marked'). Opens nothing else",
            TreasureLimits.MarkedKeyDefaultPrice, TreasureLimits.MarkedKeyDefaultUses);

        LabsPass = BindItem(config, "04. Labs pass", "04. 알리바바의 연구소 출입증", "04. Ali Baba's Labs Pass",
            "연구소(랩스) 입장만 — 장착하고 들어갈 때마다 1회씩 닳고, 문은 못 엽니다",
            "Labs entry only - one use per raid entered while it is equipped; opens no doors",
            TreasureLimits.LabsPassDefaultPrice, TreasureLimits.LabsPassDefaultUses);

        ApplyLanguage();
        Language.SettingChanged += (_, _) => ApplyLanguage();
    }

    private ItemEntries BindItem(ConfigFile config, string section, string categoryKo, string categoryEn,
        string opensKo, string opensEn, int defaultPrice, int defaultUses)
    {
        var entries = new ItemEntries();

        entries.Price = config.Bind(section, "Flea price", defaultPrice, new ConfigDescription(
            "Flea market base price in roubles.",
            new AcceptableValueRange<int>(TreasureLimits.PriceMin, TreasureLimits.PriceMax),
            Attributes(categoryKo, categoryEn, "플리마켓 가격 (₽)", "Flea price (₽)",
                $"플리마켓 기준 가격(루블). 매물은 이 가격 근처로 올라옵니다. 바꾸면 기존 매물이 새 가격으로 바로 다시 올라옵니다.\n기본 {defaultPrice:N0}₽",
                $"Flea market base price in roubles; offers are listed around it. Changing it re-lists the current offers at the new price right away.\nDefault {defaultPrice:N0}₽",
                60)));

        entries.Uses = config.Bind(section, "Uses", defaultUses, new ConfigDescription(
            "How many locks it opens before it breaks.",
            new AcceptableValueRange<int>(TreasureLimits.UsesMin, TreasureLimits.UsesMax),
            Attributes(categoryKo, categoryEn, "사용 가능 횟수", "Uses",
                $"몇 번 열면 사라지는지. 여는 대상: {opensKo}.\n'무제한 사용'을 켜면 이 값은 무시됩니다. 기본 {defaultUses}회",
                $"How many locks it opens before it breaks. Opens: {opensEn}.\nIgnored while 'Unlimited uses' is on. Default {defaultUses}",
                50)));

        entries.Unlimited = config.Bind(section, "Unlimited uses", false, new ConfigDescription(
            "Never breaks.",
            null,
            Attributes(categoryKo, categoryEn, "무제한 사용", "Unlimited uses",
                "켜면 횟수 제한 없이 계속 씁니다. (이미 가지고 있는 아이템의 툴팁 표시는 게임을 다시 시작해야 바뀔 수 있습니다)",
                "Never breaks. (The tooltip of a copy you already own may only update after a game restart.)",
                40)));

        entries.LooseChance = BindChance(config, section, "Loose loot chance", categoryKo, categoryEn,
            "맵 바닥 등장 확률 (%)", "Loose loot chance (%)",
            "레이드 한 판마다 이 확률로 맵 어딘가의 바닥 루트 1개가 이 아이템으로 바뀝니다.",
            "Each raid, with this chance one loose item somewhere on the map becomes this item.",
            30);

        entries.ContainerChance = BindChance(config, section, "Container chance", categoryKo, categoryEn,
            "컨테이너 등장 확률 (%)", "Container chance (%)",
            "레이드 한 판마다 이 확률로 컨테이너(상자·가방·금고·마크방 포함) 하나 안에 1개가 들어갑니다.",
            "Each raid, with this chance one copy is placed in some container (crates, bags, safes, marked rooms included).",
            20);

        entries.BotChance = BindChance(config, section, "Bot carry chance", categoryKo, categoryEn,
            "봇 소지 확률 (%)", "Bot carry chance (%)",
            "봇(스캐브·PMC·보스 등) 한 명이 생성될 때마다 이 확률로 주머니/조끼/가방에 1개를 갖고 나옵니다. 플레이어 스캐브는 제외.",
            "Every bot (scav, PMC, boss, ...) gets one in its pockets/rig/backpack with this chance when it spawns. Player scavs excluded.",
            10);

        return entries;
    }

    private ConfigEntry<float> BindChance(ConfigFile config, string section, string key, string categoryKo,
        string categoryEn, string nameKo, string nameEn, string helpKo, string helpEn, int order) =>
        config.Bind(section, key, 0f, new ConfigDescription(
            helpEn,
            new AcceptableValueRange<float>((float)TreasureLimits.ChanceMin, (float)TreasureLimits.ChanceMax),
            Attributes(categoryKo, categoryEn, nameKo, nameEn,
                helpKo + "\n0.1% 단위로 설정할 수 있습니다(값을 직접 입력). 0이면 절대 안 나옵니다. 기본 0 (등장 안 함). 다음 레이드부터 적용.",
                helpEn + "\nSettable in 0.1% steps (type the value). 0 means never. Default 0 (never). Applies from the next raid.",
                order)));

    private ConfigurationManagerAttributes Attributes(string categoryKo, string categoryEn, string nameKo,
        string nameEn, string helpKo, string helpEn, int order)
    {
        var attributes = new ConfigurationManagerAttributes { Order = order };
        _texts.Add(new Text
        {
            Attributes = attributes,
            CategoryKo = categoryKo,
            CategoryEn = categoryEn,
            NameKo = nameKo,
            NameEn = nameEn,
            HelpKo = helpKo,
            HelpEn = helpEn,
        });
        return attributes;
    }

    private void ApplyLanguage()
    {
        Loc.En = Loc.ResolveEnglish(Language.Value);
        foreach (var text in _texts)
        {
            text.Attributes.Category = Loc.L(text.CategoryKo, text.CategoryEn);
            text.Attributes.DispName = Loc.L(text.NameKo, text.NameEn);
            text.Attributes.Description = Loc.L(text.HelpKo, text.HelpEn);
        }
    }
}
