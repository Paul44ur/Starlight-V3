using System.Globalization;
using Starlight.Settings;

namespace Starlight.Model;

/// <summary>The command line for llama-server (plan 5.2).</summary>
public static class ServerArguments
{
    public static List<string> Build(ModelSettings model, string modelPath, string? projectorPath)
    {
        var args = new List<string>
        {
            "--model", modelPath,
            "--host", "127.0.0.1",
            "--port", model.Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", model.ContextSize.ToString(CultureInfo.InvariantCulture),
            "--n-gpu-layers", model.GpuLayers.ToString(CultureInfo.InvariantCulture),
            "--flash-attn", "on",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
            "--jinja",
            "--parallel", "1",
            "--no-webui",
        };
        if (!string.IsNullOrWhiteSpace(projectorPath))
        {
            args.Add("--mmproj");
            args.Add(projectorPath);
        }
        args.AddRange(SplitExtra(model.ExtraServerArgs));
        return args;
    }

    /// <summary>Splits extra arguments from Settings on spaces, keeping "quoted parts" together.</summary>
    internal static IEnumerable<string> SplitExtra(string extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            yield break;
        }
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in extra)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
