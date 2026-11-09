using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Foveation
{
    /// <summary>
    /// Reads and writes the user's Quad-Views-Foveated settings file, %LocalAppData%\Quad-Views-Foveated\settings.cfg.
    ///
    /// The file has one "name=value" per line and optional [section] headers; lines before the first section apply to every
    /// headset and app, so the values are kept there. Other lines are left as they are. The layer reads the file only when
    /// an XR session starts, so a change needs an XR restart (<see cref="QuadViewsToggle"/> does it).
    /// </summary>
    public static class QuadViewsLayerSettings
    {
        public static string path => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quad-Views-Foveated", "settings.cfg");

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        /// <summary>The value in the user's file, or <paramref name="fallback"/> (the layer's default) when it is not set.</summary>
        public static float Get(string key, float fallback)
        {
            try
            {
                if (!File.Exists(path))
                    return fallback;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("["))
                        break;
                    if (line.StartsWith(key + "=") &&
                        float.TryParse(line.Substring(key.Length + 1).Trim(), NumberStyles.Float, k_Inv, out var value))
                        return value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuadViews] Could not read {path}: {e.Message}");
            }
            return fallback;
        }

        /// <summary>Writes the values; a null value removes the line so the layer uses its default.</summary>
        public static bool Set(IDictionary<string, float?> values)
        {
            try
            {
                var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
                var firstSection = lines.FindIndex(l => l.Trim().StartsWith("["));
                if (firstSection < 0)
                    firstSection = lines.Count;

                foreach (var pair in values)
                {
                    var index = lines.FindIndex(0, firstSection, l => l.Trim().StartsWith(pair.Key + "="));
                    if (pair.Value is float value)
                    {
                        var line = $"{pair.Key}={value.ToString("0.###", k_Inv)}";
                        if (index >= 0)
                            lines[index] = line;
                        else
                            lines.Insert(firstSection++, line);
                    }
                    else if (index >= 0)
                    {
                        lines.RemoveAt(index);
                        firstSection--;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[QuadViews] Could not write {path}: {e.Message}");
                return false;
            }
        }
    }
}
