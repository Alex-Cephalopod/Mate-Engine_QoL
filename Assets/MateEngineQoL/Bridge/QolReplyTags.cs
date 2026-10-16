using System;
using MateEngineQoL.Tags;
using UnityEngine;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Acts on reply tags (Phase 5): [emotion:x] sets the avatar's face through <see cref="QolExpression"/>,
    /// [action:x] plays a gesture from the animation library. Also writes the tag instructions that go after the
    /// character prompt. The router strips tags whether or not this is on; with it off they are just dropped.
    /// </summary>
    public static class QolReplyTags
    {
        static string _lastMissing;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            QolChatRouter.TagReceived -= OnTag;
            QolChatRouter.TagReceived += OnTag;
            QolChatRouter.Cancelled -= OnCancelled;
            QolChatRouter.Cancelled += OnCancelled;
        }

        public static bool Enabled
        {
            get
            {
                QolServices.EnsureInitialized();
                return QolServices.Settings.Tags.Enabled;
            }
        }

        /// <summary>The tag vocabulary for the system prompt (emotions, plus the library's gestures), or "" when tags are off.</summary>
        public static string PromptInstructions()
        {
            if (!Enabled) return "";
            QolAnimationLibrary library = QolAnimationLibrary.Instance;
            return ReplyTagVocabulary.PromptText(ReplyTagVocabulary.ActionNames(library != null ? library.Catalog?.Entries : null));
        }

        static void OnTag(ReplyTag tag)
        {
            if (!Enabled) return;
            if (tag.Kind == ReplyTagKind.Emotion)
            {
                QolExpression face = QolExpression.ForActiveAvatar();
                if (face != null) face.Show(ReplyTagVocabulary.CanonicalEmotion(tag.Name));
                return;
            }

            QolAnimationLibrary library = QolAnimationLibrary.Instance;
            if (library != null && library.PlayAction(tag.Name)) return;
            if (!string.Equals(_lastMissing, tag.Name, StringComparison.Ordinal))
            {
                _lastMissing = tag.Name; // once per name in a row, so a model repeating it doesn't flood the log
                Debug.Log("[QoL Tags] No gesture for " + tag + " (or the avatar is busy).");
            }
        }

        /// <summary>A new message or a character switch: the old reply's face fades out.</summary>
        static void OnCancelled()
        {
            QolExpression face = QolExpression.ForActiveAvatar(false);
            if (face != null) face.Release();
        }
    }
}
