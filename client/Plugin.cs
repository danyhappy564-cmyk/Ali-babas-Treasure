using System;
using System.Collections.Concurrent;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace AlibabaTreasure.Client;

/// <summary>
/// Ali Baba's Treasure, client half: lets the treasures open locks and extracts (the Labs pass is
/// handled by the server's entry-key list), and is the F12 front end
/// for the server mod's prices, uses and spawn chances.
/// </summary>
[BepInPlugin(PluginGuid, "Ali Baba's Treasure", "1.3.1")]
public class TreasurePlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.rf.alibabatreasure.client";

    private const float SendDelaySeconds = 0.8f;
    private const float FetchRetrySeconds = 3f;
    private const int FetchAttempts = 40;

    internal static ManualLogSource? Log;

    private TreasureSettings _settings = null!;
    private readonly ConcurrentQueue<Action> _mainThread = new();

    // Server sync state (main thread only).
    private bool _synced;
    private bool _suppress;
    private bool _fetching;
    private int _fetchAttempts;
    private float _nextFetchAt;
    private float _sendAt = -1f;
    private bool _sending;
    private bool _sendAgain;

    // Template tuning state (main thread only).
    private bool _usesDirty = true;
    private ItemFactory? _tunedFactory;
    private float _nextTemplateCheckAt;

    private void Awake()
    {
        Log = Logger;
        _settings = new TreasureSettings(Config);

        Config.SettingChanged += (_, args) =>
        {
            if (args.ChangedSetting == _settings.Language)
            {
                return;
            }

            _usesDirty = true;
            if (!_suppress)
            {
                _sendAt = Time.realtimeSinceStartup + SendDelaySeconds;
            }
        };

        new Harmony(PluginGuid).PatchAll(typeof(TreasurePlugin).Assembly);
        Logger.LogInfo("Ali Baba's Treasure loaded - 열려라, 참깨!");
    }

    private void Update()
    {
        while (_mainThread.TryDequeue(out var action))
        {
            action();
        }

        var now = Time.realtimeSinceStartup;

        if (!_synced && !_fetching && _fetchAttempts < FetchAttempts && now >= _nextFetchAt)
        {
            Fetch();
        }

        if (_sendAt >= 0f && now >= _sendAt)
        {
            _sendAt = -1f;
            Send();
        }

        if (now >= _nextTemplateCheckAt)
        {
            _nextTemplateCheckAt = now + 1f;
            TuneTemplates();
        }
    }

    /// <summary>Reads the server's settings into F12 - the server's config.json is the source of truth.</summary>
    private void Fetch()
    {
        _fetching = true;
        _fetchAttempts++;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? answer = null;
            string? error = null;
            try
            {
                answer = ServerLink.Send(TreasureIds.RouteGet, null);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _mainThread.Enqueue(() =>
            {
                _fetching = false;
                if (answer != null && Absorb(answer))
                {
                    _synced = true;
                    Logger.LogInfo("Settings loaded from the server.");
                    return;
                }

                _nextFetchAt = Time.realtimeSinceStartup + FetchRetrySeconds;
                if (_fetchAttempts >= FetchAttempts)
                {
                    Logger.LogWarning($"Could not reach the server mod ({error ?? "bad answer"}). "
                        + "Is AlibabaTreasure installed in SPT_Runtime\\user\\mods? F12 changes will be sent once it answers.");
                }
            });
        });
    }

    /// <summary>Posts every value (debounced), then takes the server's clamped answer back.</summary>
    private void Send()
    {
        if (_sending)
        {
            _sendAgain = true;
            return;
        }

        _sending = true;
        var json = "{\"keycard\":" + _settings.Keycard.Read().ToJson()
            + ",\"key\":" + _settings.Key.Read().ToJson()
            + ",\"markedKey\":" + _settings.MarkedKey.Read().ToJson()
            + ",\"labsPass\":" + _settings.LabsPass.Read().ToJson()
            + ",\"labyrinthPass\":" + _settings.LabyrinthPass.Read().ToJson() + "}";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? answer = null;
            string? error = null;
            try
            {
                answer = ServerLink.Send(TreasureIds.RouteSet, json);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _mainThread.Enqueue(() =>
            {
                _sending = false;
                if (answer != null && Absorb(answer))
                {
                    _synced = true;
                }
                else
                {
                    Logger.LogWarning($"Server did not take the change ({error ?? answer}). It stays in the .cfg; change any value to retry.");
                }

                if (_sendAgain)
                {
                    _sendAgain = false;
                    Send();
                }
            });
        });
    }

    private bool Absorb(string answer)
    {
        if (answer.Contains("\"ok\":false"))
        {
            return false;
        }

        var keycard = ItemValues.Parse(answer, "keycard");
        var key = ItemValues.Parse(answer, "key");
        var markedKey = ItemValues.Parse(answer, "markedKey");
        var labsPass = ItemValues.Parse(answer, "labsPass");
        var labyrinthPass = ItemValues.Parse(answer, "labyrinthPass");
        if (keycard is null || key is null || markedKey is null || labsPass is null || labyrinthPass is null)
        {
            return false;
        }

        TreasureLocks.AddMarkedKeys(ItemValues.ParseIdList(answer, "markedKeys"));

        // Values written here came from the server - do not echo them back.
        _suppress = true;
        try
        {
            _settings.Keycard.Write(keycard);
            _settings.Key.Write(key);
            _settings.MarkedKey.Write(markedKey);
            _settings.LabsPass.Write(labsPass);
            _settings.LabyrinthPass.Write(labyrinthPass);
        }
        finally
        {
            _suppress = false;
        }

        _usesDirty = true;
        return true;
    }

    /// <summary>
    /// Writes the uses into the client's own item templates. A raid runs on the client, and the
    /// game reads <c>template.MaximumNumberOfUsage</c> afresh every time a lock is opened
    /// (<c>WorldInteractiveObject.UnlockOperation</c>), so this takes effect on the next use.
    /// Re-applied whenever the template table is rebuilt (a new <see cref="ItemFactory"/>).
    /// </summary>
    private void TuneTemplates()
    {
        if (!Singleton<ItemFactory>.Instantiated)
        {
            return;
        }

        var factory = Singleton<ItemFactory>.Instance;
        if (!_usesDirty && ReferenceEquals(factory, _tunedFactory))
        {
            return;
        }

        var keycard = Tune(factory, TreasureIds.KeycardId, _settings.Keycard.MaximumNumberOfUsage);
        var key = Tune(factory, TreasureIds.KeyId, _settings.Key.MaximumNumberOfUsage);
        var markedKey = Tune(factory, TreasureIds.MarkedKeyId, _settings.MarkedKey.MaximumNumberOfUsage);
        var labsPass = Tune(factory, TreasureIds.LabsPassId, _settings.LabsPass.MaximumNumberOfUsage);
        var labyrinthPass = Tune(factory, TreasureIds.LabyrinthPassId, _settings.LabyrinthPass.MaximumNumberOfUsage);
        if (keycard || key || markedKey || labsPass || labyrinthPass)
        {
            _tunedFactory = factory;
            _usesDirty = false;
        }
    }

    private static bool Tune(ItemFactory factory, string id, int maximumUses)
    {
        if (!factory.ItemTemplates.TryGetValue(id, out var template) || template is not KeyTemplate keyTemplate)
        {
            return false;
        }

        keyTemplate.MaximumNumberOfUsage = maximumUses;
        return true;
    }
}
