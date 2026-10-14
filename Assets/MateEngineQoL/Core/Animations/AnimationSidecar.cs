using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.Animations
{
    public static class AnimationCategories
    {
        public const string Idle = "idle";
        public const string Gesture = "gesture";
        public const string Sit = "sit";
        public const string Other = "other";
    }

    /// <summary>
    /// Settings for one clip, read from an optional <c>&lt;clip name&gt;.json</c> next to it. Every field is optional;
    /// a clip without a sidecar is a looping idle named after its file.
    /// </summary>
    public sealed class AnimationSidecar
    {
        public const float MinBlend = 0f;
        public const float MaxBlend = 5f;
        public const float MaxIdleWeight = 100f;

        public string Name;
        public string Category = AnimationCategories.Idle;
        public bool Loop = true;
        /// <summary>Chance of being picked as an idle, relative to the other idles. 0 = never picked.</summary>
        public float IdleWeight = 1f;
        /// <summary>Where a sit clip attaches ("taskbar", "windowTopEdge"); null for standing clips. Used in Phase 6.</summary>
        public string Anchor;
        public float BlendIn = 0.4f;
        public float BlendOut = 0.4f;
        public List<string> Tags = new List<string>();

        /// <summary>
        /// Reads a sidecar. Missing or invalid fields fall back to the defaults (an idle weight of 1 only for idles),
        /// numbers are clamped, and a broken file yields the defaults plus a one-line <paramref name="error"/>.
        /// </summary>
        public static AnimationSidecar Parse(string json, string fallbackName, out string error)
        {
            error = null;
            var s = new AnimationSidecar { Name = fallbackName };
            JObject o = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    o = JsonConvert.DeserializeObject<JToken>(json, new JsonSerializerSettings { MaxDepth = 16 }) as JObject;
                    if (o == null) error = "not a JSON object";
                }
                catch (JsonException e)
                {
                    error = e.Message;
                }
            }

            string name = Str(o, "name");
            if (!string.IsNullOrWhiteSpace(name)) s.Name = name.Trim();
            string category = Str(o, "category");
            if (!string.IsNullOrWhiteSpace(category)) s.Category = category.Trim().ToLowerInvariant();
            s.Loop = Bool(o, "loop") ?? true;
            s.IdleWeight = Clamp(Num(o, "idleWeight") ?? (s.Category == AnimationCategories.Idle ? 1f : 0f), 0f, MaxIdleWeight);
            string anchor = Str(o, "anchor");
            s.Anchor = string.IsNullOrWhiteSpace(anchor) ? null : anchor.Trim();
            s.BlendIn = Clamp(Num(o, "blendIn") ?? s.BlendIn, MinBlend, MaxBlend);
            s.BlendOut = Clamp(Num(o, "blendOut") ?? s.BlendOut, MinBlend, MaxBlend);
            if (o != null && o.TryGetValue("tags", StringComparison.OrdinalIgnoreCase, out JToken tags) && tags is JArray arr)
                foreach (JToken t in arr)
                    if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t))
                        s.Tags.Add(((string)t).Trim().ToLowerInvariant());
            return s;
        }

        static JToken Get(JObject o, string key) =>
            o != null && o.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out JToken t) ? t : null;

        static string Str(JObject o, string key) => Get(o, key) is JToken t && t.Type == JTokenType.String ? (string)t : null;

        static bool? Bool(JObject o, string key) => Get(o, key) is JToken t && t.Type == JTokenType.Boolean ? (bool)t : (bool?)null;

        static float? Num(JObject o, string key)
        {
            JToken t = Get(o, key);
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) return null;
            double d = (double)t;
            return double.IsNaN(d) || double.IsInfinity(d) ? (float?)null : (float)d;
        }

        static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
