using System.Collections.Generic;
using MateEngineQoL.Tags;
using UniVRM10;
using UnityEngine;
using VRM;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Moves the avatar's mouth to the voice: reads uLipSync's vowel ratios from <see cref="QolVoicePipeline"/> and sets
    /// the A/I/U/E/O expressions on a VRM 0.x (VRMBlendShapeProxy) or VRM 1.0 (Vrm10Instance) model.
    /// Runs after UniversalBlendshapes (which zeroes the mouth every frame when nothing animates it) and before
    /// Vrm10Instance applies expressions. Leaves the mouth alone when not speaking.
    /// When the VRM 0.x vowel clips bind nothing (or the model has no VRM expressions), mouth blendshapes on the meshes
    /// are driven directly: VRChat visemes (vrc.v_aa ...) first, then Aa/Ih/Ou/Ee/Oh-style names.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class QolLipSync : MonoBehaviour
    {
        /// <summary>Resources path of the uLipSync profile, copied from the package by MateEngine/QoL/Create Lip Sync Profile.</summary>
        public const string ProfileResource = "QolLipSyncProfile";

        const float Smoothing = 25f; // per second; higher follows the voice more tightly
        const float Gain = 1f;

        static readonly string[] Vowels = { "A", "I", "U", "E", "O" };
        static readonly BlendShapeKey[] Vrm0Keys =
        {
            BlendShapeKey.CreateFromPreset(BlendShapePreset.A), BlendShapeKey.CreateFromPreset(BlendShapePreset.I),
            BlendShapeKey.CreateFromPreset(BlendShapePreset.U), BlendShapeKey.CreateFromPreset(BlendShapePreset.E),
            BlendShapeKey.CreateFromPreset(BlendShapePreset.O),
        };
        static readonly ExpressionKey[] Vrm1Keys = { ExpressionKey.Aa, ExpressionKey.Ih, ExpressionKey.Ou, ExpressionKey.Ee, ExpressionKey.Oh };

        /// <summary>Mesh blendshape names (normalised) per vowel for the fallback, best first.</summary>
        static readonly string[][] MeshVowelNames =
        {
            new[] { "vrc_v_aa", "v_aa", "aa", "mouth_a", "a" },
            new[] { "vrc_v_ih", "v_ih", "ih", "mouth_i", "i" },
            new[] { "vrc_v_ou", "v_ou", "ou", "mouth_u", "u" },
            new[] { "vrc_v_e", "v_e", "ee", "mouth_e", "e" },
            new[] { "vrc_v_oh", "v_oh", "oh", "mouth_o", "o" },
        };

        readonly float[] _weights = new float[5];
        readonly float[] _targets = new float[5];
        readonly List<(SkinnedMeshRenderer Renderer, int[] Shapes)> _mesh = new List<(SkinnedMeshRenderer, int[])>();
        VRMBlendShapeProxy _vrm0;
        Vrm10Instance _vrm1;
        bool _searched, _touching, _warned;

        /// <summary>Adds the component to a model once (listener for <see cref="QolEvents.AvatarLoaded"/>).</summary>
        public static void Attach(GameObject model)
        {
            if (model != null && model.GetComponent<QolLipSync>() == null) model.AddComponent<QolLipSync>();
        }

        void OnEnable() => _searched = false;

        void LateUpdate()
        {
            if (!_searched)
            {
                _searched = true;
                _vrm0 = GetComponentInChildren<VRMBlendShapeProxy>(true);
                _vrm1 = GetComponentInChildren<Vrm10Instance>(true);
                // Converted models often carry the vowel clips with nothing bound; use the mesh shapes then.
                if (_vrm0 != null && !HasBoundVowels(_vrm0)) _vrm0 = null;
                _mesh.Clear();
                if (_vrm0 == null && _vrm1 == null) FindMeshVowels();
            }
            if (_vrm0 == null && _vrm1 == null && _mesh.Count == 0)
            {
                if (!_warned && QolVoicePipeline.IsSpeaking)
                {
                    _warned = true;
                    Debug.Log("[QoL Voice] " + name + " has no VRM vowel expressions or mouth blendshapes; the mouth won't move.");
                }
                return;
            }

            QolVoicePipeline pipeline = QolVoicePipeline.Instance;
            bool speaking = QolVoicePipeline.IsSpeaking && pipeline.LipSync != null;
            // The Talk animation moves the mouth while a reply streams in. With a voice on, keep it closed until the voice starts.
            bool hold = QolVoicePipeline.IsEnabled && QolChatRouter.IsReplying;
            if (speaking) ReadTargets(pipeline.LipSync.result);
            else System.Array.Clear(_targets, 0, _targets.Length);

            float t = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
            bool open = false;
            for (int i = 0; i < _weights.Length; i++)
            {
                _weights[i] = Mathf.Lerp(_weights[i], _targets[i], t);
                if (_weights[i] > 0.005f) open = true;
            }

            if (!speaking && !hold && !open)
            {
                if (!_touching) return; // never fight other scripts' mouth shapes when silent
                System.Array.Clear(_weights, 0, _weights.Length);
                Write();
                _touching = false;
                return;
            }
            _touching = true;
            Write();
        }

        void ReadTargets(uLipSync.LipSyncInfo info)
        {
            System.Array.Clear(_targets, 0, _targets.Length);
            Dictionary<string, float> ratios = info.phonemeRatios;
            if (ratios == null) return;
            float volume = Mathf.Clamp01(info.volume) * Gain;
            foreach (KeyValuePair<string, float> pair in ratios)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                int index = System.Array.IndexOf(Vowels, pair.Key.Substring(0, 1).ToUpperInvariant());
                if (index >= 0) _targets[index] = Mathf.Clamp01(_targets[index] + pair.Value * volume);
            }
        }

        void Write()
        {
            if (_vrm0 != null)
            {
                for (int i = 0; i < Vrm0Keys.Length; i++) _vrm0.ImmediatelySetValue(Vrm0Keys[i], _weights[i]);
            }
            else if (_vrm1 != null && _vrm1.Runtime != null)
            {
                var expression = _vrm1.Runtime.Expression;
                for (int i = 0; i < Vrm1Keys.Length; i++) expression.SetWeight(Vrm1Keys[i], _weights[i]);
            }
            else
            {
                foreach (var (renderer, shapes) in _mesh)
                {
                    if (renderer == null) continue;
                    for (int i = 0; i < shapes.Length; i++)
                        if (shapes[i] >= 0) renderer.SetBlendShapeWeight(shapes[i], _weights[i] * 100f);
                }
            }
        }

        static bool HasBoundVowels(VRMBlendShapeProxy proxy)
        {
            BlendShapeAvatar avatar = proxy.BlendShapeAvatar;
            if (avatar == null) return false;
            foreach (BlendShapeKey key in Vrm0Keys)
            {
                BlendShapeClip clip = avatar.GetClip(key);
                if (clip != null && ((clip.Values != null && clip.Values.Length > 0) || (clip.MaterialValues != null && clip.MaterialValues.Length > 0)))
                    return true;
            }
            return false;
        }

        void FindMeshVowels()
        {
            foreach (SkinnedMeshRenderer smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                var byName = new Dictionary<string, int>();
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string n = TagParser.NormalizeName(mesh.GetBlendShapeName(i));
                    if (n != null && !byName.ContainsKey(n)) byName[n] = i;
                }
                var shapes = new int[5];
                bool any = false;
                for (int v = 0; v < 5; v++)
                {
                    shapes[v] = -1;
                    foreach (string want in MeshVowelNames[v])
                        if (byName.TryGetValue(want, out int index)) { shapes[v] = index; any = true; break; }
                }
                if (any) _mesh.Add((smr, shapes));
            }
            if (_mesh.Count > 0) Debug.Log("[QoL Voice] " + name + ": lip sync drives mouth blendshapes on " + _mesh.Count + " mesh(es).");
        }
    }
}
