using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MateEngineQoL.Animations
{
    /// <summary>One clip in the animation folder.</summary>
    public sealed class AnimationEntry
    {
        /// <summary>Path relative to the folder with forward slashes; the key for favourites and settings.</summary>
        public string Id;
        public string FilePath;
        public long Size;
        public DateTime ModifiedUtc;
        public AnimationSidecar Info;
        /// <summary>Why the sidecar was ignored, or null.</summary>
        public string SidecarError;

        public bool IsIdle => Info.IdleWeight > 0f;
    }

    /// <summary>
    /// The <c>.vrma</c> clips under one folder (subfolders included) with their sidecars. Watches the folder; changes
    /// are picked up by <see cref="PollChanges"/> on the caller's thread once the files have been quiet for a moment,
    /// so a clip still being copied isn't read half-written.
    /// </summary>
    public sealed class AnimationCatalog : IDisposable
    {
        public const string ClipExtension = ".vrma";
        public const long MaxClipBytes = 64L * 1024 * 1024;
        public const long MaxSidecarBytes = 64 * 1024;
        public const int MaxClips = 500;
        const int MaxDepth = 4;
        static readonly TimeSpan Debounce = TimeSpan.FromSeconds(0.75);

        public string Folder { get; }
        public IReadOnlyList<AnimationEntry> Entries => _entries;
        /// <summary>Clips skipped on the last scan (too big, unreadable, over the limit), one line each.</summary>
        public IReadOnlyList<string> Skipped => _skipped;

        List<AnimationEntry> _entries = new List<AnimationEntry>();
        List<string> _skipped = new List<string>();
        string _signature = "";
        FileSystemWatcher _watcher;
        readonly object _lock = new object();
        bool _dirty;
        DateTime _lastEventUtc;

        public AnimationCatalog(string folder)
        {
            Folder = Path.GetFullPath(folder);
        }

        /// <summary>Reads the folder again. Returns true when the clips or their sidecars changed.</summary>
        public bool Rescan()
        {
            var entries = new List<AnimationEntry>();
            var skipped = new List<string>();
            var sig = new StringBuilder();
            if (Directory.Exists(Folder)) Scan(Folder, 0, entries, skipped, sig);
            entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

            _entries = entries;
            _skipped = skipped;
            string s = sig.ToString();
            bool changed = s != _signature;
            _signature = s;
            return changed;
        }

        void Scan(string dir, int depth, List<AnimationEntry> entries, List<string> skipped, StringBuilder sig)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + ClipExtension); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                if (!file.EndsWith(ClipExtension, StringComparison.OrdinalIgnoreCase)) continue; // "*.vrma" also matches "*.vrmax" on Windows
                string id = RelativeId(file);
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                    if (entries.Count >= MaxClips) { skipped.Add(id + ": more than " + MaxClips + " clips"); continue; }
                    if (info.Length > MaxClipBytes) { skipped.Add(id + ": larger than " + MaxClipBytes / (1024 * 1024) + " MB"); continue; }

                    string name = Path.GetFileNameWithoutExtension(file);
                    string sidecarPath = Path.Combine(dir, name + ".json");
                    string json = null, sidecarError = null;
                    var sidecar = new FileInfo(sidecarPath);
                    if (sidecar.Exists)
                    {
                        if (sidecar.Length > MaxSidecarBytes) sidecarError = "sidecar larger than 64 KB";
                        else json = File.ReadAllText(sidecarPath);
                        sig.Append(sidecar.LastWriteTimeUtc.Ticks).Append(':').Append(sidecar.Length);
                    }

                    AnimationSidecar parsed = AnimationSidecar.Parse(json, name, out string parseError);
                    entries.Add(new AnimationEntry
                    {
                        Id = id,
                        FilePath = file,
                        Size = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc,
                        Info = parsed,
                        SidecarError = sidecarError ?? parseError,
                    });
                    sig.Append('|').Append(id).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    skipped.Add(id + ": " + e.Message);
                }
            }

            if (depth >= MaxDepth) return;
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string sub in dirs)
            {
                // Don't follow junctions or symlinks out of the folder.
                if ((File.GetAttributes(sub) & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0) continue;
                Scan(sub, depth + 1, entries, skipped, sig);
            }
        }

        string RelativeId(string file)
        {
            string rel = file.Length > Folder.Length ? file.Substring(Folder.Length).TrimStart('\\', '/') : file;
            return rel.Replace('\\', '/');
        }

        public AnimationEntry Find(string id)
        {
            if (id == null) return null;
            foreach (AnimationEntry e in _entries)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Creates the folder if needed and watches it for changes.</summary>
        public void StartWatching()
        {
            if (_watcher != null) return;
            Directory.CreateDirectory(Folder);
            _watcher = new FileSystemWatcher(Folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.Error += (_, __) => MarkDirty(); // buffer overflow: rescan everything
            _watcher.EnableRaisingEvents = true;
        }

        void OnFsEvent(object sender, FileSystemEventArgs e) => MarkDirty();

        /// <summary>Marks the folder as changed, as if the watcher had seen a change.</summary>
        public void MarkDirty()
        {
            lock (_lock)
            {
                _dirty = true;
                _lastEventUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Call regularly from one thread. Rescans once changes have been quiet for a moment; returns true when the
        /// clips changed.
        /// </summary>
        public bool PollChanges(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (!_dirty || nowUtc - _lastEventUtc < Debounce) return false;
                _dirty = false;
            }
            return Rescan();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public static class IdlePicker
    {
        /// <summary>
        /// Picks an idle by weight, avoiding <paramref name="currentId"/> when another idle exists. Null when none.
        /// </summary>
        public static AnimationEntry Pick(IReadOnlyList<AnimationEntry> entries, string currentId, Random rng)
        {
            float total = 0f;
            int count = 0;
            foreach (AnimationEntry e in entries)
                if (e.IsIdle) { total += e.Info.IdleWeight; count++; }
            if (count == 0) return null;

            bool skipCurrent = count > 1 && currentId != null;
            if (skipCurrent)
            {
                total = 0f;
                foreach (AnimationEntry e in entries)
                    if (e.IsIdle && !IsCurrent(e, currentId)) total += e.Info.IdleWeight;
            }

            double r = rng.NextDouble() * total;
            AnimationEntry last = null;
            foreach (AnimationEntry e in entries)
            {
                if (!e.IsIdle || (skipCurrent && IsCurrent(e, currentId))) continue;
                last = e;
                r -= e.Info.IdleWeight;
                if (r < 0) return e;
            }
            return last;
        }

        static bool IsCurrent(AnimationEntry e, string id) => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase);
    }
}
