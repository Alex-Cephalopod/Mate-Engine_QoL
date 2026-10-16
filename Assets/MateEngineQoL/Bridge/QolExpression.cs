using System;
using System.Collections.Generic;
using MateEngineQoL.Tags;
using UniVRM10;
using UnityEngine;
using VRM;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Shows the emotion a reply's [emotion:x] tag asks for: a VRM 0.x BlendShape clip (VRMBlendShapeProxy) or a VRM 1.0
    /// expression (Vrm10Instance), blended in, held while the reply streams and is spoken plus
    /// <see cref="Settings.TagSettings.HoldSeconds"/>, then faded out. Standard emotions map to presets; any other name is
    /// looked up among the model's custom clips. When the VRM expression is missing or binds nothing, same-named mesh
    /// blendshapes (Joy, Sorrow, Surprised, ...) are driven directly. Runs after UniversalBlendshapes, which writes Joy/Angry/Sorrow/Fun every
    /// frame from the Face Layer, and keeps the larger of the two, so hover faces still show; before QolLipSync.
    /// </summary>
    [DefaultExecutionOrder(9990)]
    public sealed class QolExpression : MonoBehaviour
    {
        const float BlendInSpeed = 8f;  // per second, exponential
        const float BlendOutSpeed = 3f;

        /// <summary>One expression this component drives.</summary>
        sealed class Slot
        {
            public string Emotion;
            public Action<float> Write;
            /// <summary>What upstream's UniversalBlendshapes asks for on the same expression this frame, or null if it doesn't drive it.</summary>
            public Func<float> Upstream;
            public float Weight, Target;
            public bool Touching;
        }

        readonly List<Slot> _slots = new List<Slot>();
        readonly HashSet<string> _missing = new HashSet<string>(StringComparer.Ordinal);
        VRMBlendShapeProxy _vrm0;
        Vrm10Instance _vrm1;
        UniversalBlendshapes _upstream;
        bool _searched;
        string _emotion;
        float _releaseAt;

        /// <summary>The component on the visible avatar, added on first use unless <paramref name="create"/> is false.</summary>
        public static QolExpression ForActiveAvatar(bool create = true)
        {
            var avatar = FindFirstObjectByType<AvatarAnimatorController>();
            if (avatar == null) return null;
            var face = avatar.GetComponent<QolExpression>();
            if (face == null && create) face = avatar.gameObject.AddComponent<QolExpression>();
            return face;
        }

        /// <summary>Blends to an emotion (a canonical name or a custom expression's normalised name); neutral releases.</summary>
        public void Show(string emotion)
        {
            Search();
            float intensity = Mathf.Clamp01(QolServices.Settings.Tags.ExpressionIntensity);
            Slot slot = emotion == ReplyTagVocabulary.Neutral ? null : SlotFor(emotion);
            foreach (Slot s in _slots) s.Target = s == slot ? intensity : 0f;
            _emotion = slot != null ? emotion : null;
            _releaseAt = Time.unscaledTime + HoldSeconds;
        }

        /// <summary>Fades the current emotion out.</summary>
        public void Release()
        {
            foreach (Slot s in _slots) s.Target = 0f;
            _emotion = null;
        }

        static float HoldSeconds => Mathf.Clamp(QolServices.Settings.Tags.HoldSeconds, 0f, 60f);

        void OnEnable() => _searched = false;

        void Search()
        {
            if (_searched) return;
            _searched = true;
            _slots.Clear();
            _missing.Clear();
            _vrm0 = GetComponentInChildren<VRMBlendShapeProxy>(true);
            _vrm1 = GetComponentInChildren<Vrm10Instance>(true);
            _upstream = GetComponentInChildren<UniversalBlendshapes>(true);
        }

        void LateUpdate()
        {
            if (!_searched || _slots.Count == 0) return;

            if (_emotion != null)
            {
                bool busy = QolChatRouter.IsReplying || QolVoicePipeline.IsSpeaking;
                if (busy) _releaseAt = Time.unscaledTime + HoldSeconds;
                else if (Time.unscaledTime >= _releaseAt || !QolReplyTags.Enabled) Release();
            }

            foreach (Slot s in _slots)
            {
                float speed = s.Target > s.Weight ? BlendInSpeed : BlendOutSpeed;
                s.Weight = Mathf.Lerp(s.Weight, s.Target, 1f - Mathf.Exp(-speed * Time.deltaTime));
                if (s.Target == 0f && s.Weight < 0.005f) s.Weight = 0f;

                if (s.Weight <= 0f)
                {
                    if (!s.Touching) continue; // never fight other scripts when idle
                    s.Touching = false;
                    // Hand the expression back: upstream rewrites its own keys next frame, the rest go to zero now.
                    if (s.Upstream == null) Safe(s, 0f);
                    continue;
                }
                s.Touching = true;
                float upstream = s.Upstream != null ? Mathf.Clamp01(s.Upstream()) : 0f;
                Safe(s, Mathf.Max(s.Weight, upstream));
            }
        }

        void Safe(Slot s, float value)
        {
            try { s.Write(value); }
            catch (Exception e)
            {
                // A model torn down mid-frame; drop the slot instead of logging every frame.
                Debug.LogWarning("[QoL Tags] Can't set expression " + s.Emotion + ": " + e.Message);
                s.Write = _ => { };
            }
        }

        Slot SlotFor(string emotion)
        {
            if (string.IsNullOrEmpty(emotion)) return null;
            foreach (Slot s in _slots)
                if (s.Emotion == emotion) return s;
            if (_missing.Contains(emotion)) return null;

            Slot slot = (_vrm0 != null ? Vrm0Slot(emotion) : _vrm1 != null ? Vrm1Slot(emotion) : null) ?? MeshSlot(emotion);
            if (slot == null)
            {
                _missing.Add(emotion);
                Debug.Log("[QoL Tags] " + name + " has no expression for [emotion:" + emotion + "].");
                return null;
            }
            slot.Emotion = emotion;
            _slots.Add(slot);
            return slot;
        }

        Slot Vrm0Slot(string emotion)
        {
            BlendShapeAvatar avatar = _vrm0.BlendShapeAvatar;
            if (avatar == null || avatar.Clips == null) return null;

            BlendShapePreset preset;
            Func<float> upstream = null;
            UniversalBlendshapes ub = _upstream;
            switch (emotion)
            {
                case "happy": preset = BlendShapePreset.Joy; if (ub != null) upstream = () => ub.Joy; break;
                case "angry": preset = BlendShapePreset.Angry; if (ub != null) upstream = () => ub.Angry; break;
                case "sad": preset = BlendShapePreset.Sorrow; if (ub != null) upstream = () => ub.Sorrow; break;
                case "relaxed": preset = BlendShapePreset.Fun; if (ub != null) upstream = () => ub.Fun; break;
                default: preset = BlendShapePreset.Unknown; break;
            }

            BlendShapeClip clip = null;
            foreach (BlendShapeClip c in avatar.Clips)
            {
                if (c == null) continue;
                bool match = preset != BlendShapePreset.Unknown
                    ? c.Preset == preset
                    : c.Preset == BlendShapePreset.Unknown && TagParser.NormalizeName(c.BlendShapeName) == emotion;
                if (match) { clip = c; break; }
            }
            // Converted models often carry the preset clips with nothing bound; the mesh fallback handles those.
            if (clip == null || ((clip.Values == null || clip.Values.Length == 0) && (clip.MaterialValues == null || clip.MaterialValues.Length == 0)))
                return null;

            BlendShapeKey key = BlendShapeKey.CreateFromClip(clip);
            VRMBlendShapeProxy proxy = _vrm0;
            return new Slot { Write = v => proxy.ImmediatelySetValue(key, v), Upstream = upstream };
        }

        Slot Vrm1Slot(string emotion)
        {
            Vrm10RuntimeExpression expression = _vrm1.Runtime?.Expression;
            if (expression == null) return null;

            ExpressionKey? key = null;
            Func<float> upstream = null;
            UniversalBlendshapes ub = _upstream;
            switch (emotion)
            {
                case "happy": key = ExpressionKey.Happy; if (ub != null) upstream = () => ub.Joy; break;
                case "angry": key = ExpressionKey.Angry; if (ub != null) upstream = () => ub.Angry; break;
                case "sad": key = ExpressionKey.Sad; if (ub != null) upstream = () => ub.Sorrow; break;
                case "relaxed": key = ExpressionKey.Relaxed; if (ub != null) upstream = () => ub.Fun; break;
                case "surprised": key = ExpressionKey.Surprised; break;
            }

            ExpressionKey? found = null;
            foreach (ExpressionKey k in expression.ExpressionKeys)
            {
                bool match = key.HasValue
                    ? k.Equals(key.Value)
                    : k.Preset == ExpressionPreset.custom && TagParser.NormalizeName(k.Name) == emotion;
                if (match) { found = k; break; }
            }
            if (!found.HasValue) return null;

            ExpressionKey use = found.Value;
            return new Slot { Write = v => expression.SetWeight(use, v), Upstream = upstream };
        }

        /// <summary>Mesh blendshape names (normalised) tried for each standard emotion when the VRM expression is missing or empty.</summary>
        static readonly Dictionary<string, string[]> MeshNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "happy", new[] { "joy", "happy", "smile" } },
            { "angry", new[] { "angry", "anger" } },
            { "sad", new[] { "sorrow", "sad" } },
            { "relaxed", new[] { "fun", "relaxed" } },
            { "surprised", new[] { "surprised", "surprise" } },
        };

        /// <summary>
        /// Drives same-named blendshapes on the model's meshes directly (0..100), for models whose VRM expression clips
        /// are missing or bind nothing (common in converted models). Every mesh with a matching shape is driven.
        /// </summary>
        Slot MeshSlot(string emotion)
        {
            string[] names = MeshNames.TryGetValue(emotion, out string[] n) ? n : new[] { emotion };
            var targets = new List<(SkinnedMeshRenderer Renderer, int Index)>();
            foreach (SkinnedMeshRenderer smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null) continue;
                foreach (string want in names)
                {
                    int index = -1;
                    for (int i = 0; i < mesh.blendShapeCount && index < 0; i++)
                        if (TagParser.NormalizeName(mesh.GetBlendShapeName(i)) == want) index = i;
                    if (index < 0) continue;
                    targets.Add((smr, index));
                    break;
                }
            }
            if (targets.Count == 0) return null;
            return new Slot
            {
                Write = v =>
                {
                    foreach (var (renderer, index) in targets)
                        if (renderer != null) renderer.SetBlendShapeWeight(index, v * 100f);
                },
            };
        }
    }
}
