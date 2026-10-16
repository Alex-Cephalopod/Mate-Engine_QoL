using System;
using System.Collections.Generic;
using System.Text;
using MateEngineQoL.Animations;

namespace MateEngineQoL.Tags
{
    /// <summary>
    /// What the model may put in tags: the standard emotions (the VRM 1.0 expression presets plus neutral), the
    /// gestures in the animation library, and the system-prompt lines that tell the model about both.
    /// </summary>
    public static class ReplyTagVocabulary
    {
        public const string Neutral = "neutral";

        /// <summary>Emotions listed in the prompt; each maps to a VRM preset (VRM 0.x has no surprised preset, a clip named so is used).</summary>
        public static readonly string[] Emotions = { "happy", "sad", "angry", "surprised", "relaxed", Neutral };

        /// <summary>At most this many gestures are listed in the prompt.</summary>
        public const int MaxPromptActions = 30;

        static readonly Dictionary<string, string> EmotionAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "joy", "happy" }, { "joyful", "happy" }, { "glad", "happy" }, { "smile", "happy" }, { "smiling", "happy" },
            { "excited", "happy" }, { "cheerful", "happy" }, { "fun", "relaxed" },
            { "sorrow", "sad" }, { "sadness", "sad" }, { "upset", "sad" }, { "crying", "sad" }, { "unhappy", "sad" },
            { "anger", "angry" }, { "mad", "angry" }, { "annoyed", "angry" }, { "furious", "angry" },
            { "surprise", "surprised" }, { "shocked", "surprised" }, { "shock", "surprised" }, { "amazed", "surprised" },
            { "calm", "relaxed" }, { "content", "relaxed" }, { "peaceful", "relaxed" },
            { "normal", Neutral }, { "none", Neutral }, { "default", Neutral }, { "reset", Neutral },
        };

        /// <summary>A standard emotion for a tag's (normalised) name; any other name is returned unchanged, for a custom expression.</summary>
        public static string CanonicalEmotion(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return EmotionAliases.TryGetValue(name, out string canonical) ? canonical : name;
        }

        /// <summary>A clip can be an action when its category is gesture.</summary>
        public static bool IsAction(AnimationEntry entry) =>
            entry?.Info != null && entry.Info.Category == AnimationCategories.Gesture;

        /// <summary>The name a gesture goes by in tags and in the prompt ("Big Wave" → "big_wave").</summary>
        public static string ActionName(AnimationEntry entry) => TagParser.NormalizeName(entry?.Info?.Name);

        /// <summary>
        /// The gesture an [action:name] tag means: a gesture whose name matches, else the first one with that tag
        /// (sidecar "tags"). Null when there is none. Entries are expected in catalog order.
        /// </summary>
        public static AnimationEntry FindAction(IEnumerable<AnimationEntry> entries, string name)
        {
            if (entries == null || string.IsNullOrEmpty(name)) return null;
            AnimationEntry byTag = null;
            foreach (AnimationEntry e in entries)
            {
                if (!IsAction(e)) continue;
                if (ActionName(e) == name) return e;
                if (byTag == null)
                    foreach (string t in e.Info.Tags)
                        if (TagParser.NormalizeName(t) == name) { byTag = e; break; }
            }
            return byTag;
        }

        /// <summary>The distinct gesture names, in catalog order, at most <see cref="MaxPromptActions"/>.</summary>
        public static List<string> ActionNames(IEnumerable<AnimationEntry> entries)
        {
            var names = new List<string>();
            if (entries == null) return names;
            foreach (AnimationEntry e in entries)
            {
                if (!IsAction(e)) continue;
                string n = ActionName(e);
                if (n != null && !names.Contains(n)) names.Add(n);
                if (names.Count >= MaxPromptActions) break;
            }
            return names;
        }

        /// <summary>
        /// The lines appended to the system prompt at request time. Emotions are always offered; the action line
        /// only when the library has gestures.
        /// </summary>
        public static string PromptText(IReadOnlyList<string> actions)
        {
            var sb = new StringBuilder();
            sb.Append("You can show feelings and gestures with tags. Begin a reply with [emotion:NAME] to set your facial expression, where NAME is one of: ")
              .Append(string.Join(", ", Emotions)).Append('.');
            if (actions != null && actions.Count > 0)
            {
                sb.Append(" Add [action:NAME] to play a gesture, where NAME is one of: ")
                  .Append(string.Join(", ", actions)).Append('.');
            }
            sb.Append(" Use at most one of each per reply and only when it fits. Tags are hidden from the user and never spoken; don't mention them.");
            return sb.ToString();
        }
    }
}
