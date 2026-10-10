using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace AlibabaTreasure.Server;

/// <summary>
/// Puts the treasures into the maps' own loot tables, the way LotsofLoot (ArchangelWTF) adds its
/// marked-room extra items: as weighted entries the game's normal draw can pick.
/// </summary>
/// <remarks>
/// <para>
/// Loose loot: every spawn point picks one item from its pool by <c>RelativeProbability</c>.
/// Containers: every slot a container type fills is drawn from that type's <c>ItemDistribution</c>.
/// A treasure is added only to pools that already hold a key or keycard (key spots, jackets,
/// drawers, safes, marked rooms...), so it turns up where keys naturally do.
/// </para>
/// <para>
/// The F12 percentage is the treasure's share of each such draw: weight = S·p / (100 − p), where S
/// is the summed weight of the pool's own items. 1% therefore means "one key-spot draw in a
/// hundred is this item". 0 adds nothing.
/// </para>
/// <para>
/// The tables are SPT <c>LazyLoad</c>s: transformers run whenever a table is built, reading the
/// settings at that moment. After an F12 change <see cref="Refresh"/> drops any cached copy so the
/// next raid's tables are rebuilt with the new weights.
/// </para>
/// </remarks>
[Injectable(InjectionType.Singleton)]
public class TreasurePools(ISptLogger<TreasurePools> logger, TemplateTable templates, LocationTable locationTable)
{
    private static readonly MongoId KeyParent = new(TreasureIds.KeyParentId);
    private static readonly MongoId KeycardParent = new(TreasureIds.KeycardParentId);

    // Logged once per map and table, when debug is on (tables can rebuild every raid).
    private readonly HashSet<string> _reported = [];

    public void Register()
    {
        foreach (var (name, location) in locationTable.GetDictionary())
        {
            var map = name;
            location?.LooseLoot?.AddTransformer(loose =>
            {
                if (loose is not null)
                {
                    Safe(map, "loose", () => InjectLoose(map, loose));
                }

                return loose!;
            });

            location?.StaticLoot?.AddTransformer(containers =>
            {
                if (containers is not null)
                {
                    Safe(map, "container", () => InjectContainers(map, containers));
                }

                return containers!;
            });
        }
    }

    /// <summary>Drops cached tables so the next build uses the current chances.</summary>
    public void Refresh()
    {
        _reported.Clear();
        foreach (var (_, location) in locationTable.GetDictionary())
        {
            location?.LooseLoot?.Clear();
            location?.StaticLoot?.Clear();
        }
    }

    internal static double Weight(double poolWeight, double percent)
    {
        var p = Math.Min(percent, 99.0);
        return p <= 0 || poolWeight <= 0 ? 0 : poolWeight * p / (100.0 - p);
    }

    private bool IsKeyLike(MongoId template) =>
        !TreasureIds.IsTreasure(template.ToString())
        && templates.Items.TryGetValue(template, out var item)
        && (item.Parent == KeyParent || item.Parent == KeycardParent);

    internal void InjectLoose(string map, LooseLoot loose)
    {
        if (loose.Spawnpoints is null)
        {
            return;
        }

        var settings = TreasureConfigStore.Current;
        var chances = TreasureSpawn.Items
            .Select(entry => (entry.Id, Percent: entry.Settings(settings).LooseChance))
            .Where(entry => entry.Percent > 0)
            .ToList();
        if (chances.Count == 0)
        {
            return;
        }

        var pools = 0;
        foreach (var point in loose.Spawnpoints)
        {
            if (point.Template?.Items is null || point.ItemDistribution is null)
            {
                continue;
            }

            var items = point.Template.Items.Where(item => !TreasureIds.IsTreasure(item.Template.ToString())).ToList();
            if (!items.Any(item => IsKeyLike(item.Template)))
            {
                continue;
            }

            var keys = new HashSet<string?>(items.Select(item => item.ComposedKey));
            var distribution = point.ItemDistribution.Where(d => keys.Contains(d.ComposedKey?.Key)).ToList();
            var poolWeight = distribution.Sum(d => d.RelativeProbability ?? 0);

            foreach (var (id, percent) in chances)
            {
                var composed = new MongoId().ToString();
                items.Add(new SptLootItem { Id = new MongoId(composed), Template = new MongoId(id), ComposedKey = composed });
                distribution.Add(new LooseLootItemDistribution
                {
                    ComposedKey = new ComposedKey { Key = composed },
                    RelativeProbability = Weight(poolWeight, percent),
                });
            }

            point.Template.Items = items;
            point.ItemDistribution = distribution;
            pools++;
        }

        Report(map, "loose", $"{pools} loose-loot spot(s) that can hold keys now include {Describe(chances)}");
    }

    internal void InjectContainers(string map, Dictionary<MongoId, StaticLootDetails> containers)
    {
        var settings = TreasureConfigStore.Current;
        var chances = TreasureSpawn.Items
            .Select(entry => (entry.Id, Percent: entry.Settings(settings).ContainerChance))
            .Where(entry => entry.Percent > 0)
            .ToList();
        if (chances.Count == 0)
        {
            return;
        }

        var types = 0;
        foreach (var (_, details) in containers)
        {
            if (details?.ItemDistribution is null)
            {
                continue;
            }

            var distribution = details.ItemDistribution.Where(d => !TreasureIds.IsTreasure(d.Tpl.ToString())).ToList();
            if (!distribution.Any(d => IsKeyLike(d.Tpl)))
            {
                continue;
            }

            var poolWeight = distribution.Sum(d => (double)(d.RelativeProbability ?? 0));
            foreach (var (id, percent) in chances)
            {
                distribution.Add(new ItemDistribution
                {
                    Tpl = new MongoId(id),
                    RelativeProbability = (float)Weight(poolWeight, percent),
                });
            }

            details.ItemDistribution = distribution;
            types++;
        }

        Report(map, "container", $"{types} container type(s) that can hold keys now include {Describe(chances)}");
    }

    private static string Describe(List<(string Id, double Percent)> chances) =>
        string.Join(", ", chances.Select(c => $"{c.Id} {c.Percent}%"));

    private void Report(string map, string table, string message)
    {
        if (TreasureConfigStore.Current.Debug && _reported.Add(map + "|" + table))
        {
            logger.Info($"{TreasureMod.LogPrefix} [debug] {map}: {message}");
        }
    }

    private void Safe(string map, string table, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Never take a map's loot table down over this mod.
            logger.Warning($"{TreasureMod.LogPrefix} could not add treasures to the {table} table of {map}: {ex.Message}");
        }
    }
}
