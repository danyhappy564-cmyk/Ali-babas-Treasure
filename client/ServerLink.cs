using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AlibabaTreasure.Client;

/// <summary>
/// Plain HTTP to the server mod's settings routes. No spt-common needed: the backend address comes
/// from the game's <c>-config={"BackendUrl":...}</c> launch argument, and the
/// <c>requestcompressed: 0</c> / <c>responsecompressed: 0</c> headers make SPT exchange plain JSON
/// instead of zlib (kb 10.6, same as CompoundingPerf). Blocking - call it from a thread-pool
/// thread, never the main thread.
/// </summary>
internal static class ServerLink
{
    private static string? _backend;

    private static string? BackendUrl => _backend ??= FindBackend();

    private static string? FindBackend()
    {
        // spt-common has already parsed the launch argument; read it by reflection so this plugin
        // still needs no spt-common reference.
        try
        {
            var host = Type.GetType("SPT.Common.Http.RequestHandler, spt-common")?
                .GetField("Host", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
                .GetValue(null) as string;
            if (!string.IsNullOrEmpty(host))
            {
                return host!.TrimEnd('/');
            }
        }
        catch (Exception)
        {
            // fall back to reading the argument ourselves
        }

        // Windows argument parsing can strip the quotes out of -config={...}.
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            var match = Regex.Match(arg, "BackendUrl[\"']?\\s*:\\s*[\"']?([^\"',}\\s]+)");
            if (match.Success)
            {
                return match.Groups[1].Value.TrimEnd('/');
            }
        }

        return null;
    }

    public static string Send(string path, string? json)
    {
        var backend = BackendUrl ?? throw new InvalidOperationException("no -config BackendUrl launch argument");
        var request = (HttpWebRequest)WebRequest.Create(backend + path);
        request.Method = json is null ? "GET" : "POST";
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        request.Headers["requestcompressed"] = "0";
        request.Headers["responsecompressed"] = "0";

        // Mono waits for a "100 Continue" before sending a POST body; the body is tiny, just send it.
        request.ServicePoint.Expect100Continue = false;

        // SPT serves a self-signed certificate; trust it for this request only.
        request.ServerCertificateValidationCallback = (_, _, _, _) => true;

        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            request.ContentType = "application/json";
            request.ContentLength = bytes.Length;
            using var body = request.GetRequestStream();
            body.Write(bytes, 0, bytes.Length);
        }

        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream()!, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>One item's settings as they travel over the wire.</summary>
internal sealed class ItemValues
{
    public int Price;
    public int Uses;
    public bool Unlimited;
    public double LooseChance;
    public double ContainerChance;
    public double BotChance;

    public string ToJson() => string.Format(
        CultureInfo.InvariantCulture,
        "{{\"price\":{0},\"uses\":{1},\"unlimited\":{2},\"looseChance\":{3},\"containerChance\":{4},\"botChance\":{5}}}",
        Price, Uses, Unlimited ? "true" : "false", LooseChance, ContainerChance, BotChance);

    /// <summary>
    /// Reads the <c>"name": { ... }</c> block out of the server's answer. The answer is flat and
    /// written by our own server mod, so a pattern match is enough and keeps the plugin free of a
    /// JSON library whose version would have to match the game's.
    /// </summary>
    public static ItemValues? Parse(string json, string name)
    {
        var block = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\\{([^}]*)\\}");
        if (!block.Success)
        {
            return null;
        }

        var body = block.Groups[1].Value;
        return new ItemValues
        {
            Price = (int)Number(body, "price"),
            Uses = (int)Number(body, "uses"),
            Unlimited = Regex.IsMatch(body, "\"unlimited\"\\s*:\\s*true"),
            LooseChance = Number(body, "looseChance"),
            ContainerChance = Number(body, "containerChance"),
            BotChance = Number(body, "botChance"),
        };
    }

    /// <summary>Reads the <c>"markedKeys": [ ... ]</c> id list out of the server's answer.</summary>
    public static string[] ParseIdList(string json, string name)
    {
        var block = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!block.Success)
        {
            return Array.Empty<string>();
        }

        var ids = Regex.Matches(block.Groups[1].Value, "\"([0-9a-fA-F]{24})\"");
        var result = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            result[i] = ids[i].Groups[1].Value;
        }

        return result;
    }

    private static double Number(string body, string field)
    {
        var match = Regex.Match(body, "\"" + field + "\"\\s*:\\s*(-?[0-9.eE+]+)");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
