using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sdo.Osu;
using UnityEngine;
using UnityEngine.Networking;

namespace Sdo.Game
{
    /// <summary>
    /// Loads and caches custom audio referenced by one osu! beatmap. Legacy keysound maps can disguise Ogg Vorbis
    /// data behind a .wav extension, so decoder selection is always based on <see cref="AudioFileType.Of"/>.
    /// </summary>
    public sealed class OsuKeysoundBank : IDisposable
    {
        // Bound native decoder/file-handle pressure while loading hundreds of small keysounds.
        public const int LoadBatchSize = 16;
        private const long MaxNormalizerInputBytes = 8L * 1024 * 1024;

        private readonly Dictionary<string, AudioClip> _clips =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private readonly List<UnityWebRequest> _pendingRequests = new List<UnityWebRequest>();
        private readonly HashSet<string> _temporaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, Mp3Pcm> _mp3Decoder;
        private string _temporaryDirectory;
        private int _temporarySerial;
        private int _generation;

        public OsuKeysoundBank(Func<string, Mp3Pcm> mp3Decoder = null)
        {
            _mp3Decoder = mp3Decoder ??
                (path => Mp3Decoder.Decode(path, Mp3Decoder.Mp3Sync.Osu));
        }

        public int ReferencedCount { get; private set; }
        public int LoadedCount => _clips.Count;
        public int MissingCount { get; private set; }

        public bool TryGet(string filename, out AudioClip clip)
            => _clips.TryGetValue(NormalizeName(filename), out clip) && clip != null;

        /// <summary>Unique filenames needed by both storyboard events and playable note heads.</summary>
        public static List<string> ReferencedFilenames(OsuBeatmap map)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (map == null) return result;

            foreach (var sample in map.SampleEvents) Add(sample.Filename);
            foreach (var hitObject in map.HitObjects) Add(hitObject.CustomSampleFilename);
            return result;

            void Add(string name)
            {
                name = NormalizeName(name);
                if (name.Length > 0 && seen.Add(name)) result.Add(name);
            }
        }

        /// <summary>
        /// Resolve a beatmap-relative sample without allowing rooted paths or traversal outside the beatmap folder.
        /// Returns an empty string for a missing or unsafe reference.
        /// </summary>
        public static string ResolveSamplePath(string chartPath, string sampleFilename)
        {
            if (string.IsNullOrWhiteSpace(chartPath) || string.IsNullOrWhiteSpace(sampleFilename)) return "";
            try
            {
                string root = Path.GetFullPath(Path.GetDirectoryName(chartPath) ?? "");
                string relative = NormalizeName(sampleFilename).Replace('/', Path.DirectorySeparatorChar);
                if (root.Length == 0 || relative.Length == 0 || Path.IsPathRooted(relative)) return "";

                string full = Path.GetFullPath(Path.Combine(root, relative));
                string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;
                var comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!full.StartsWith(rootPrefix, comparison) || HasReparsePointBelowRoot(root, full)) return "";
                return File.Exists(full) ? full : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Unity decoder type selected from file contents, not its potentially false extension.</summary>
        public static AudioType DecoderType(string path)
        {
            switch (AudioFileType.Of(path))
            {
                case AudioKind.Ogg: return AudioType.OGGVORBIS;
                case AudioKind.Wav: return AudioType.WAV;
                case AudioKind.Mp3: return AudioType.MPEG;
                default: return AudioType.UNKNOWN;
            }
        }

        /// <summary>Load every distinct referenced sample in small concurrent batches.</summary>
        public IEnumerator Load(OsuBeatmap map, string chartPath)
        {
            Release();
            int generation = _generation;
            var names = ReferencedFilenames(map);
            ReferencedCount = names.Count;
            if (names.Count == 0) yield break;

            try
            {
                for (int offset = 0; offset < names.Count; offset += LoadBatchSize)
                {
                    var pending = new List<Pending>(LoadBatchSize);
                    int end = Math.Min(names.Count, offset + LoadBatchSize);
                    for (int i = offset; i < end; i++)
                    {
                        if (generation != _generation) yield break;
                        string name = names[i];
                        string path = ResolveSamplePath(chartPath, name);
                        AudioType type = path.Length > 0 ? DecoderType(path) : AudioType.UNKNOWN;
                        if (path.Length == 0 || type == AudioType.UNKNOWN)
                        {
                            MissingCount++;
                            continue;
                        }

                        string decoderPath = path;
                        UnityWebRequest request = null;
                        bool started = false;
                        try
                        {
                            if (type == AudioType.MPEG)
                            {
                                var task = Task.Run(() => _mp3Decoder(path));
                                pending.Add(new Pending(name, path, task));
                            }
                            else
                            {
                                decoderPath = PrepareDecoderPath(path, type);
                                request = UnityWebRequestMultimedia.GetAudioClip(
                                    SdoExtracted.FileUri(decoderPath), type);
                                var operation = request.SendWebRequest();
                                _pendingRequests.Add(request);
                                pending.Add(new Pending(name, decoderPath, request, operation));
                            }
                            started = true;
                        }
                        catch
                        {
                            MissingCount++;
                            if (request != null)
                            {
                                _pendingRequests.Remove(request);
                                try { request.Dispose(); } catch { }
                            }
                            ReleaseTemporaryFile(decoderPath);
                        }
                        // Keep previously-started requests in flight, but spread synchronous
                        // file sniffing and Ogg header normalization across frames.
                        if (started) yield return null;
                    }

                    bool waiting;
                    do
                    {
                        waiting = false;
                        if (generation != _generation) yield break;
                        foreach (var item in pending)
                        {
                            if (item.Failed) continue;
                            try
                            {
                                if (!item.IsDone) waiting = true;
                            }
                            catch
                            {
                                item.Failed = true;
                            }
                        }
                        if (waiting) yield return null;
                    } while (waiting);

                    foreach (var item in pending)
                    {
                        bool failed = item.Failed;
                        try
                        {
                            AudioClip clip = null;
                            if (failed)
                            {
                                failed = true;
                            }
                            else if (item.Mp3Task != null)
                            {
                                clip = Mp3Decoder.ToClip(item.Mp3Task.Result, "osu:" + item.Name);
                                if (clip == null) failed = true;
                            }
                            else if (item.Request.result != UnityWebRequest.Result.Success)
                            {
                                failed = true;
                            }
                            else
                            {
                                clip = DownloadHandlerAudioClip.GetContent(item.Request);
                                if (clip == null) failed = true;
                            }

                            if (!failed && clip != null)
                            {
                                clip.name = "osu:" + item.Name;
                                _clips[item.Name] = clip;
                            }
                        }
                        catch
                        {
                            failed = true;
                        }
                        finally
                        {
                            if (item.Request != null)
                            {
                                _pendingRequests.Remove(item.Request);
                                try { item.Request.Dispose(); } catch { }
                            }
                            ReleaseTemporaryFile(item.DecoderPath);
                        }
                        if (failed) MissingCount++;
                    }
                }
            }
            finally
            {
                // Release/another Load already owns cleanup after advancing the generation.
                // An old iterator must never abort requests or delete temp files of the new load.
                if (generation == _generation)
                {
                    AbortPendingRequests();
                    ReleaseTemporaryFiles();
                }
            }
        }

        public void Dispose() => Release();

        private string PrepareDecoderPath(string path, AudioType type)
        {
            if (type != AudioType.OGGVORBIS) return path;
            try
            {
                long length = new FileInfo(path).Length;
                if (length <= 0 || length > MaxNormalizerInputBytes) return path;
                if (!LegacyOggNormalizer.TryNormalize(File.ReadAllBytes(path), out var normalized)) return path;
                if (string.IsNullOrEmpty(_temporaryDirectory))
                {
                    _temporaryDirectory = Path.Combine(Application.temporaryCachePath,
                        "sdo-osu-keysounds-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_temporaryDirectory);
                }

                string temp = Path.Combine(_temporaryDirectory,
                    (_temporarySerial++).ToString("D4") + ".ogg");
                File.WriteAllBytes(temp, normalized);
                _temporaryFiles.Add(temp);
                return temp;
            }
            catch
            {
                return path; // fall back to Unity's normal decoder path; its result is counted below.
            }
        }

        private void ReleaseTemporaryFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !_temporaryFiles.Contains(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (!File.Exists(path)) _temporaryFiles.Remove(path);
            }
            catch { }
        }

        private void ReleaseTemporaryFiles()
        {
            foreach (string path in new List<string>(_temporaryFiles))
                ReleaseTemporaryFile(path);
            if (!string.IsNullOrEmpty(_temporaryDirectory))
            {
                try
                {
                    if (_temporaryFiles.Count == 0 && Directory.Exists(_temporaryDirectory))
                        Directory.Delete(_temporaryDirectory, false);
                    if (!Directory.Exists(_temporaryDirectory))
                    {
                        _temporaryDirectory = null;
                        _temporarySerial = 0;
                    }
                }
                catch { }
            }
        }

        private void AbortPendingRequests()
        {
            foreach (var request in new List<UnityWebRequest>(_pendingRequests))
            {
                if (request == null) continue;
                try { request.Abort(); } catch { }
                try { request.Dispose(); } catch { }
            }
            _pendingRequests.Clear();
        }

        private void Release()
        {
            _generation++;
            AbortPendingRequests();
            ReleaseTemporaryFiles();

            foreach (var clip in _clips.Values)
            {
                if (clip == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(clip);
                else UnityEngine.Object.DestroyImmediate(clip);
            }
            _clips.Clear();
            ReferencedCount = 0;
            MissingCount = 0;
        }

        private static string NormalizeName(string filename)
        {
            string value = (filename ?? "").Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            return value.Replace('\\', '/').Trim();
        }

        private static bool HasReparsePointBelowRoot(string root, string fullPath)
        {
            string relative = fullPath.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = root;
            foreach (string component in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!File.Exists(current) && !Directory.Exists(current)) return false;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }

        private sealed class Pending
        {
            public readonly string Name;
            public readonly string DecoderPath;
            public readonly UnityWebRequest Request;
            public readonly UnityWebRequestAsyncOperation Operation;
            public readonly Task<Mp3Pcm> Mp3Task;
            public bool Failed;

            public bool IsDone => Mp3Task != null ? Mp3Task.IsCompleted : Operation.isDone;

            public Pending(string name, string decoderPath, UnityWebRequest request,
                UnityWebRequestAsyncOperation operation)
            {
                Name = name;
                DecoderPath = decoderPath;
                Request = request;
                Operation = operation;
            }

            public Pending(string name, string decoderPath, Task<Mp3Pcm> mp3Task)
            {
                Name = name;
                DecoderPath = decoderPath;
                Mp3Task = mp3Task;
            }
        }
    }
}
