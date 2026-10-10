using System;
using System.Collections.Generic;
using EFT;

namespace AlibabaTreasure.Client;

/// <summary>
/// F12 "Debug mode": explains every decision the plugin makes, as <c>[Alibaba]</c> lines in
/// <c>BepInEx/LogOutput.log</c>. Off by default; nothing is formatted while it is off.
/// </summary>
internal static class Dbg
{
    public static bool On;

    // Last line written per topic. Some checks (the door menu, extract requirements) run again and
    // again while you look at the same thing; only a change of outcome is worth a line.
    private static readonly Dictionary<string, string> Last = new();

    public static void Log(string message)
    {
        if (On)
        {
            TreasurePlugin.Log?.LogInfo("[Alibaba] " + message);
        }
    }

    /// <summary>Logs <paramref name="message"/> unless it is the same as the last one for <paramref name="topic"/>.</summary>
    public static void Once(string topic, string message)
    {
        if (!On || (Last.TryGetValue(topic, out var previous) && previous == message))
        {
            return;
        }

        Last[topic] = message;
        Log(message);
    }

    /// <summary>"Ali Baba's Key (6ac9…)" — the item's in-game name plus its id, for log lines.</summary>
    public static string Name(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId))
        {
            return "(none)";
        }

        try
        {
            return $"{(templateId + " Name").Localized()} ({templateId})";
        }
        catch (Exception)
        {
            return templateId!;
        }
    }

    public static string Uses(int used, int maximum) =>
        maximum <= 0 ? $"used {used}, unlimited" : $"{Math.Max(0, maximum - used)}/{maximum} left";
}
