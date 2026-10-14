using System;
using System.Collections.Generic;
using System.IO;
using MateEngineQoL.Animations;
using MateEngineQoL.Settings;
using UnityEngine;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// The animation library (Phase 4): watches &lt;data folder&gt;/Animations for <c>.vrma</c> clips, plays the idles on
    /// the avatar while upstream has it standing idle, and hands control back the moment upstream needs it (drag, dance,
    /// talk, sit, sleep: anything that leaves the Animator's Idle state).
    /// </summary>
    public sealed class QolAnimationLibrary : MonoBehaviour
    {
        public const string FolderName = "Animations";
        const int MaxCachedClips = 8;

        public static QolAnimationLibrary Instance { get; private set; }

        /// <summary>Raised on the main thread when clips are added, removed or edited.</summary>
        public event Action CatalogChanged;

        public AnimationCatalog Catalog { get; private set; }
        public string Folder => Catalog?.Folder;
        /// <summary>The clip the idle layer is playing, or null.</summary>
        public AnimationEntry CurrentIdle { get; private set; }
        /// <summary>Why the last clip failed to load, or null.</summary>
        public string LastError { get; private set; }

        readonly Dictionary<string, (QolVrmaClip Clip, DateTime Modified)> _cache = new Dictionary<string, (QolVrmaClip, DateTime)>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _cacheOrder = new List<string>();
        readonly HashSet<string> _broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly System.Random _rng = new System.Random();

        AvatarAnimatorController _avatar;
        QolPoseRetarget _retarget;
        float _nextAvatarSearch;
        float _idleStartedAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            try
            {
                var go = new GameObject("QoL Animation");
                DontDestroyOnLoad(go);
                go.AddComponent<QolAnimationLibrary>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[QoL Animation] Not installed: " + e.Message);
            }
        }

        void Awake()
        {
            Instance = this;
            QolServices.EnsureInitialized();
            try
            {
                Catalog = new AnimationCatalog(Path.Combine(SaveLoadHandler.DataDirectory, FolderName));
                Catalog.StartWatching();
                Catalog.Rescan();
                foreach (string s in Catalog.Skipped) Debug.LogWarning("[QoL Animation] Skipped " + s);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[QoL Animation] Can't watch the animation folder: " + e.Message);
            }
            QolEvents.AvatarLoaded += OnAvatarLoaded;
        }

        void OnDestroy()
        {
            QolEvents.AvatarLoaded -= OnAvatarLoaded;
            Catalog?.Dispose();
            foreach (var c in _cache.Values) c.Clip.Dispose();
            _cache.Clear();
            if (Instance == this) Instance = null;
        }

        void OnAvatarLoaded(GameObject model)
        {
            // The old avatar's retarget goes away with it; start over on the new one.
            _avatar = null;
            _retarget = null;
            CurrentIdle = null;
            _nextAvatarSearch = 0f;
        }

        void Update()
        {
            if (Catalog != null && Catalog.PollChanges(DateTime.UtcNow)) OnCatalogChanged();

            if ((_avatar == null || !_avatar.isActiveAndEnabled) && Time.unscaledTime >= _nextAvatarSearch)
            {
                _nextAvatarSearch = Time.unscaledTime + 1f;
                _avatar = FindFirstObjectByType<AvatarAnimatorController>();
                _retarget = null;
                CurrentIdle = null;
                if (_avatar != null && _avatar.animator != null)
                {
                    _retarget = _avatar.animator.GetComponent<QolPoseRetarget>()
                        ?? _avatar.animator.gameObject.AddComponent<QolPoseRetarget>();
                }
            }
            if (_retarget == null) return;

            bool idle = _avatar.isIdle && !_avatar.isDragging && !_avatar.isDancing;
            if (!idle || !QolServices.Settings.Animation.LibraryIdles || Catalog == null)
            {
                if (CurrentIdle != null)
                {
                    // Drag and dance take over at once; anything else (talk, sit, sleep) blends out.
                    _retarget.ReleaseBase(_avatar.isDragging || _avatar.isDancing);
                    CurrentIdle = null;
                }
                return;
            }

            float switchAfter = Mathf.Max(5f, QolServices.Settings.Animation.IdleSwitchSeconds);
            bool due = CurrentIdle == null
                || (PinnedIdle == null && Time.unscaledTime - _idleStartedAt >= switchAfter)
                || (!CurrentIdle.Info.Loop && _retarget.BaseClip != null
                    && _retarget.BaseClip.Time >= _retarget.BaseClip.Length - CurrentIdle.Info.BlendOut);
            if (due) PlayNextIdle();
        }

        void PlayNextIdle()
        {
            _idleStartedAt = Time.unscaledTime;
            AnimationEntry next = PinnedIdle != null ? Catalog.Find(PinnedIdle) : null;
            if (next == null)
            {
                var usable = new List<AnimationEntry>();
                foreach (AnimationEntry e in Catalog.Entries)
                    if (!_broken.Contains(e.Id)) usable.Add(e);
                next = IdlePicker.Pick(usable, CurrentIdle?.Id, _rng);
            }
            if (next == null)
            {
                if (CurrentIdle != null) _retarget.ReleaseBase(false);
                CurrentIdle = null;
                return;
            }
            if (next == CurrentIdle && next.Info.Loop) return; // the only idle keeps looping

            QolVrmaClip clip = Load(next);
            if (clip == null) return;
            CurrentIdle = next;
            _retarget.PlayBase(clip, next.Info.Loop, next.Info.BlendIn, next.Info.BlendOut);
        }

        /// <summary>Plays a clip once on the upper body (a gesture), over whatever the avatar is doing. Phase 5 uses this.</summary>
        public bool PlayGesture(string id)
        {
            AnimationEntry entry = Catalog?.Find(id);
            if (entry == null || _retarget == null) return false;
            QolVrmaClip clip = Load(entry);
            if (clip == null) return false;
            _retarget.PlayGesture(clip, false, entry.Info.BlendIn, entry.Info.BlendOut);
            return true;
        }

        /// <summary>Keeps one clip as the idle instead of rotating (the panel's "set as idle"); null rotates again.</summary>
        public void PinIdle(string id)
        {
            QolSettings s = QolServices.Settings;
            s.Animation.PinnedIdle = string.IsNullOrEmpty(id) ? null : id;
            QolServices.SaveSettings(s);
            if (CurrentIdle != null && !string.Equals(CurrentIdle.Id, id, StringComparison.OrdinalIgnoreCase))
                PlayNextIdle();
        }

        public string PinnedIdle => QolServices.Settings.Animation.PinnedIdle;

        /// <summary>Plays a clip on the full body now (the panel's preview); it stays until the idle switches or upstream takes over.</summary>
        public bool Preview(string id)
        {
            AnimationEntry entry = Catalog?.Find(id);
            if (entry == null || _retarget == null) return false;
            QolVrmaClip clip = Load(entry);
            if (clip == null) return false;
            CurrentIdle = entry;
            _idleStartedAt = Time.unscaledTime;
            _retarget.PlayBase(clip, entry.Info.Loop, entry.Info.BlendIn, entry.Info.BlendOut);
            return true;
        }

        public void Reload()
        {
            Catalog?.MarkDirty();
            if (Catalog != null && Catalog.Rescan()) OnCatalogChanged();
        }

        QolVrmaClip Load(AnimationEntry entry)
        {
            if (_cache.TryGetValue(entry.Id, out var cached))
            {
                if (cached.Modified == entry.ModifiedUtc) { Touch(entry.Id); return cached.Clip; }
                Evict(entry.Id);
            }

            QolVrmaClip clip;
            try
            {
                // ImmediateCaller: UniVRM loads synchronously, so the task is already done.
                var task = QolVrmaClip.LoadAsync(entry.FilePath, transform);
                if (!task.IsCompleted) throw new InvalidOperationException("load did not finish synchronously");
                clip = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _broken.Add(entry.Id);
                LastError = entry.Id + ": " + e.Message;
                Debug.LogWarning("[QoL Animation] Can't load " + LastError);
                return null;
            }

            _cache[entry.Id] = (clip, entry.ModifiedUtc);
            Touch(entry.Id);
            while (_cacheOrder.Count > MaxCachedClips) Evict(_cacheOrder[0]);
            return clip;
        }

        void Touch(string id)
        {
            _cacheOrder.Remove(id);
            _cacheOrder.Add(id);
        }

        void Evict(string id)
        {
            _cacheOrder.Remove(id);
            if (!_cache.TryGetValue(id, out var c)) return;
            _cache.Remove(id);
            if (_retarget != null) _retarget.Forget(c.Clip);
            if (CurrentIdle != null && string.Equals(CurrentIdle.Id, id, StringComparison.OrdinalIgnoreCase)) CurrentIdle = null;
            c.Clip.Dispose();
        }

        void OnCatalogChanged()
        {
            _broken.Clear(); // a fixed file gets another try
            foreach (string id in new List<string>(_cache.Keys))
            {
                AnimationEntry e = Catalog.Find(id);
                if (e == null || e.ModifiedUtc != _cache[id].Modified) Evict(id);
            }
            // A sidecar edit (weight, loop) applies on the next pick.
            if (CurrentIdle != null) CurrentIdle = Catalog.Find(CurrentIdle.Id) is AnimationEntry fresh && _cache.ContainsKey(fresh.Id) ? fresh : null;
            foreach (string s in Catalog.Skipped) Debug.LogWarning("[QoL Animation] Skipped " + s);
            CatalogChanged?.Invoke();
        }
    }
}
