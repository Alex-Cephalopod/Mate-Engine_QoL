using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngineQoL.Import
{
    /// <summary>
    /// One-way copy of a Mate Engine data folder (e.g. the Steam install's LocalLow/Shinymoon/MateEngineX)
    /// into the fork's own data folder. The source is only ever read. Existing destination files are never
    /// overwritten. Absolute paths inside .json files that point into the source are rewritten to the destination.
    /// </summary>
    public static class DataFolderImport
    {
        /// <summary>Files that are machine/session specific or regenerated, so not worth copying.</summary>
        public static readonly HashSet<string> SkippedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Player.log",
            "Player-prev.log",
            "SteamDRM.token",   // DRM ticket for the Steam build; the fork creates its own
            "ZomeAI.cache",     // LLM KV cache tied to the model binary; rebuilt on demand
        };

        public sealed class Result
        {
            public int Copied;
            public int Rewritten;
            public int SkippedExisting;
            public int SkippedExcluded;
            public long BytesCopied;
            public readonly List<string> Errors = new List<string>();
        }

        /// <summary>
        /// Copies every file under <paramref name="sourceRoot"/> into <paramref name="destRoot"/>.
        /// Throws if the folders are the same or nested inside each other.
        /// </summary>
        public static Result Run(string sourceRoot, string destRoot)
        {
            string src = Path.GetFullPath(sourceRoot);
            string dst = Path.GetFullPath(destRoot);
            if (!Directory.Exists(src))
                throw new DirectoryNotFoundException("Source data folder not found: " + src);
            if (IsSameOrInside(src, dst) || IsSameOrInside(dst, src))
                throw new ArgumentException("Source and destination folders must be separate: " + src + " / " + dst);

            var result = new Result();
            CopyDirectory(new DirectoryInfo(src), src, dst, result);
            return result;
        }

        static void CopyDirectory(DirectoryInfo dir, string srcRoot, string dstRoot, Result result)
        {
            foreach (FileInfo file in dir.GetFiles())
            {
                // Never follow links: they could point outside the source folder.
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { result.SkippedExcluded++; continue; }
                if (SkippedFileNames.Contains(file.Name)) { result.SkippedExcluded++; continue; }

                string relative = file.FullName.Substring(srcRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string target = Path.Combine(dstRoot, relative);
                if (File.Exists(target)) { result.SkippedExisting++; continue; }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = File.ReadAllText(file.FullName);
                        string rewritten = RewritePaths(text, srcRoot, dstRoot);
                        if (rewritten != null && rewritten != text)
                        {
                            File.WriteAllText(target, rewritten);
                            result.Rewritten++;
                        }
                        else
                        {
                            File.Copy(file.FullName, target, false);
                        }
                    }
                    else
                    {
                        File.Copy(file.FullName, target, false);
                    }
                    result.Copied++;
                    result.BytesCopied += file.Length;
                }
                catch (Exception e)
                {
                    result.Errors.Add(relative + ": " + e.Message);
                }
            }

            foreach (DirectoryInfo sub in dir.GetDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { result.SkippedExcluded++; continue; }
                CopyDirectory(sub, srcRoot, dstRoot, result);
            }
        }

        /// <summary>
        /// Returns <paramref name="json"/> with every string value that is a path under <paramref name="sourceRoot"/>
        /// moved under <paramref name="destRoot"/>. Returns null if the text is not valid JSON.
        /// </summary>
        public static string RewritePaths(string json, string sourceRoot, string destRoot)
        {
            JToken root;
            try { root = JToken.Parse(json); }
            catch (JsonReaderException) { return null; }

            string srcNorm = Normalize(sourceRoot);
            string dstOut = destRoot.Replace('\\', '/').TrimEnd('/');
            bool changed = false;

            foreach (JValue value in EnumerateStrings(root))
            {
                string s = (string)value.Value;
                string norm = Normalize(s);
                if (!norm.StartsWith(srcNorm, StringComparison.OrdinalIgnoreCase)) continue;
                // Require a separator (or the end) right after the prefix, so "...\MateEngineXtra" is not matched.
                if (norm.Length > srcNorm.Length && norm[srcNorm.Length] != '/') continue;

                // Normalize() swaps separators 1:1, so the prefix has the same length in the original string.
                value.Value = dstOut + s.Substring(srcNorm.Length);
                changed = true;
            }

            if (!changed) return json;
            Formatting formatting = json.Contains("\n") ? Formatting.Indented : Formatting.None;
            return root.ToString(formatting);
        }

        static IEnumerable<JValue> EnumerateStrings(JToken token)
        {
            if (token is JValue v)
            {
                if (v.Type == JTokenType.String) yield return v;
                yield break;
            }
            foreach (JToken child in token.Children())
                foreach (JValue s in EnumerateStrings(child))
                    yield return s;
        }

        static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

        static bool IsSameOrInside(string path, string root)
        {
            string p = Normalize(path) + "/";
            string r = Normalize(root) + "/";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
    }
}
