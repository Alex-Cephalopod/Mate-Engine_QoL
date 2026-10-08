using System.Collections.Generic;
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

        readonly float[] _weights = new float[5];
        readonly float[] _targets = new float[5];
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
            }
            if (_vrm0 == null && _vrm1 == null)
            {
                if (!_warned && QolVoicePipeline.IsSpeaking)
                {
                    _warned = true;
                    Debug.Log("[QoL Voice] " + name + " has no VRM expressions; the mouth won't move.");
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
        }
    }
}
