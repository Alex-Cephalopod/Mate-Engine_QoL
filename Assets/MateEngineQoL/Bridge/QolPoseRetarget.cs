using UnityEngine;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Copies <see cref="QolVrmaClip"/> poses onto this avatar each frame, on top of whatever upstream's Animator
    /// produced. Works through Mecanim muscles, so it needs only a humanoid Animator and fits VRM 0.x and 1.0 alike.
    /// Two layers: a full-body base (library idles; crossfades from one clip to the next) and an upper-body gesture
    /// layer (spine, head and arms only) that plays over the base or over upstream's own animation, e.g. while talking.
    /// Each layer eases in and out by its own weight; at zero weight upstream's pose is left untouched.
    /// Runs in LateUpdate before upstream's: AvatarMouseTracking (0) adds head, spine and eye turns on top of the posed
    /// bones; QolLipSync (10000) and Vrm10Instance (11000) come later still.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class QolPoseRetarget : MonoBehaviour
    {
        sealed class Layer
        {
            public QolVrmaClip Clip;
            public bool Loop;
            public float Weight;
            public float Target;
            public float BlendIn = 0.4f;
            public float BlendOut = 0.4f;

            public void Step(float dt)
            {
                float time = Target > Weight ? BlendIn : BlendOut;
                Weight = time <= 0f ? Target : Mathf.MoveTowards(Weight, Target, dt / time);
            }
        }

        readonly Layer _base = new Layer();
        readonly Layer _gesture = new Layer();
        // The clip the base layer is crossfading away from, and how far the fade is (0 = all previous, 1 = all current).
        QolVrmaClip _previous;
        float _fade = 1f;

        Animator _animator;
        Avatar _boundAvatar;
        HumanPoseHandler _handler;
        HumanPose _avatarPose;
        HumanPose _clipPose;
        HumanPose _previousPose;

        static bool[] _upperBody;

        public QolVrmaClip BaseClip => _base.Clip;
        public float BaseWeight => _base.Weight;
        public QolVrmaClip GestureClip => _gesture.Clip;
        public bool GestureActive => _gesture.Clip != null && (_gesture.Target > 0f || _gesture.Weight > 0f);

        /// <summary>Plays a clip on the full-body layer, crossfading from the one playing.</summary>
        public void PlayBase(QolVrmaClip clip, bool loop, float blendIn, float blendOut)
        {
            if (clip == null) { ReleaseBase(false); return; }
            if (clip != _base.Clip)
            {
                if (_base.Clip != null && _base.Weight > 0f)
                {
                    _previous = _base.Clip;
                    _fade = 0f;
                }
                else
                {
                    _previous = null;
                    _fade = 1f;
                }
                _base.Clip = clip;
                clip.Play(loop);
            }
            _base.Loop = loop;
            _base.BlendIn = blendIn;
            _base.BlendOut = blendOut;
            _base.Target = 1f;
        }

        /// <summary>Hands the full body back to upstream's animation; <paramref name="now"/> skips the blend (drag, dance).</summary>
        public void ReleaseBase(bool now)
        {
            _base.Target = 0f;
            if (now) _base.Weight = 0f;
        }

        /// <summary>Plays a clip on the upper body only. A one-shot clip fades out by itself at its end.</summary>
        public void PlayGesture(QolVrmaClip clip, bool loop, float blendIn, float blendOut)
        {
            if (clip == null) { StopGesture(false); return; }
            _gesture.Clip = clip;
            _gesture.Loop = loop;
            _gesture.BlendIn = blendIn;
            _gesture.BlendOut = blendOut;
            _gesture.Target = 1f;
            clip.Play(loop);
        }

        public void StopGesture(bool now)
        {
            _gesture.Target = 0f;
            if (now) _gesture.Weight = 0f;
        }

        /// <summary>Forgets a clip that is about to be disposed.</summary>
        public void Forget(QolVrmaClip clip)
        {
            if (clip == null) return;
            if (_base.Clip == clip) { _base.Clip = null; _base.Weight = _base.Target = 0f; }
            if (_previous == clip) { _previous = null; _fade = 1f; }
            if (_gesture.Clip == clip) { _gesture.Clip = null; _gesture.Weight = _gesture.Target = 0f; }
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            _base.Step(dt);
            if (_previous != null)
            {
                _fade = _base.BlendIn <= 0f ? 1f : Mathf.MoveTowards(_fade, 1f, dt / _base.BlendIn);
                if (_fade >= 1f) { if (_previous != _gesture.Clip) _previous.Stop(); _previous = null; }
            }
            if (_gesture.Clip != null && !_gesture.Loop && _gesture.Target > 0f
                && _gesture.Clip.Time >= _gesture.Clip.Length - _gesture.BlendOut)
                _gesture.Target = 0f;
            _gesture.Step(dt);
            if (_gesture.Clip != null && _gesture.Target <= 0f && _gesture.Weight <= 0f)
            {
                if (_gesture.Clip != _base.Clip && _gesture.Clip != _previous) _gesture.Clip.Stop();
                _gesture.Clip = null;
            }
            if (_base.Clip != null && _base.Target <= 0f && _base.Weight <= 0f)
            {
                if (_base.Clip != _gesture.Clip) _base.Clip.Stop();
                _base.Clip = null;
                _previous = null;
            }

            bool baseOn = _base.Clip != null && _base.Weight > 0f;
            bool gestureOn = _gesture.Clip != null && _gesture.Weight > 0f;
            if ((!baseOn && !gestureOn) || !Bind()) return;

            _handler.GetHumanPose(ref _avatarPose);
            if (_avatarPose.muscles == null) return;

            if (baseOn)
            {
                _base.Clip.GetPose(ref _clipPose);
                if (_previous != null)
                {
                    _previous.GetPose(ref _previousPose);
                    Mix(ref _clipPose, ref _previousPose, 1f - Mathf.SmoothStep(0f, 1f, _fade), false);
                }
                // Body placement is relative to each rig's root, so the clip's hip sway and bob carry over while the
                // avatar stays wherever upstream (dragging, sitting, the window) put its root.
                Mix(ref _avatarPose, ref _clipPose, Mathf.SmoothStep(0f, 1f, _base.Weight), false);
            }
            if (gestureOn)
            {
                _gesture.Clip.GetPose(ref _clipPose);
                Mix(ref _avatarPose, ref _clipPose, Mathf.SmoothStep(0f, 1f, _gesture.Weight), true);
            }
            _handler.SetHumanPose(ref _avatarPose);
        }

        /// <summary>Moves <paramref name="into"/> toward <paramref name="from"/> by <paramref name="w"/>.</summary>
        static void Mix(ref HumanPose into, ref HumanPose from, float w, bool upperBodyOnly)
        {
            if (from.muscles == null || into.muscles == null || w <= 0f) return;
            bool[] upper = upperBodyOnly ? UpperBody() : null;
            int n = Mathf.Min(into.muscles.Length, from.muscles.Length);
            for (int i = 0; i < n; i++)
                if (upper == null || (i < upper.Length && upper[i]))
                    into.muscles[i] = Mathf.Lerp(into.muscles[i], from.muscles[i], w);
            if (upperBodyOnly) return;
            into.bodyPosition = Vector3.Lerp(into.bodyPosition, from.bodyPosition, w);
            into.bodyRotation = Quaternion.Slerp(into.bodyRotation, from.bodyRotation, w);
        }

        /// <summary>Muscles of the spine and everything above it (arms, hands, fingers, neck, head, eyes, jaw).</summary>
        static bool[] UpperBody()
        {
            if (_upperBody != null) return _upperBody;
            var mask = new bool[HumanTrait.MuscleCount];
            for (int i = 0; i < mask.Length; i++)
            {
                int bone = HumanTrait.BoneFromMuscle(i);
                switch ((HumanBodyBones)bone)
                {
                    case HumanBodyBones.Hips:
                    case HumanBodyBones.LeftUpperLeg:
                    case HumanBodyBones.RightUpperLeg:
                    case HumanBodyBones.LeftLowerLeg:
                    case HumanBodyBones.RightLowerLeg:
                    case HumanBodyBones.LeftFoot:
                    case HumanBodyBones.RightFoot:
                    case HumanBodyBones.LeftToes:
                    case HumanBodyBones.RightToes:
                        break;
                    default:
                        mask[i] = bone >= 0;
                        break;
                }
            }
            return _upperBody = mask;
        }

        bool Bind()
        {
            if (_animator == null) _animator = GetComponent<Animator>();
            if (_animator == null || _animator.avatar == null || !_animator.avatar.isHuman) return false;
            if (_handler != null && _boundAvatar == _animator.avatar) return true;
            _handler?.Dispose();
            _handler = new HumanPoseHandler(_animator.avatar, _animator.transform);
            _boundAvatar = _animator.avatar;
            return true;
        }

        void OnDestroy() => _handler?.Dispose();
    }
}
