using System;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// A <c>.vrma</c> loaded at runtime as a pose source. UniVRM builds a hidden humanoid rig with a legacy
    /// <see cref="Animation"/>; the clip plays on that rig and <see cref="GetPose"/> reads it as Mecanim muscles,
    /// which <see cref="QolPoseRetarget"/> copies onto an avatar of either VRM version.
    /// </summary>
    public sealed class QolVrmaClip : IDisposable
    {
        public string Path { get; }
        public float Length { get; }

        readonly RuntimeGltfInstance _instance;
        readonly Animation _animation;
        readonly AnimationState _state;
        readonly HumanPoseHandler _handler;

        QolVrmaClip(string path, RuntimeGltfInstance instance, Animation animation, Animator animator)
        {
            Path = path;
            _instance = instance;
            _animation = animation;
            foreach (AnimationState s in animation) { _state = s; break; }
            Length = _state != null ? _state.length : 0f;
            _handler = new HumanPoseHandler(animator.avatar, animator.transform);
        }

        public static async Task<QolVrmaClip> LoadAsync(string path, Transform parent)
        {
            RuntimeGltfInstance instance;
            using (GltfData data = new GlbFileParser(path).Parse())
            using (var loader = new VrmAnimationImporter(data))
                instance = await loader.LoadAsync(new ImmediateCaller());

            GameObject root = instance.gameObject;
            var animation = root.GetComponent<Animation>();
            var animator = root.GetComponent<Animator>();
            if (animation == null || animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                instance.Dispose();
                throw new InvalidOperationException("not a humanoid VRM animation");
            }

            root.name = "QoL VRMA " + System.IO.Path.GetFileNameWithoutExtension(path);
            if (parent != null) root.transform.SetParent(parent, false);
            root.GetComponent<Vrm10AnimationInstance>()?.ShowBoxMan(false);
            // The rig's own Animator has no controller; it only describes the bones. The legacy Animation samples the clip.
            animator.enabled = false;
            animation.playAutomatically = false;
            animation.cullingType = AnimationCullingType.AlwaysAnimate;
            return new QolVrmaClip(path, instance, animation, animator);
        }

        public void Play(bool loop)
        {
            if (_state == null) return;
            _state.wrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
            _state.time = 0f;
            _animation.Play(_state.name);
        }

        public void Stop()
        {
            if (_state != null) _animation.Stop();
        }

        public bool IsPlaying => _state != null && _animation.isPlaying;
        public float Time => _state != null ? _state.time : 0f;

        public void GetPose(ref HumanPose pose) => _handler.GetHumanPose(ref pose);

        public void Dispose()
        {
            _handler.Dispose();
            if (_instance != null) _instance.Dispose();
        }
    }
}
