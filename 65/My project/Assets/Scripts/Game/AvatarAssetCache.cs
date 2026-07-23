using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Shared read cache + background PREFETCH for avatar assets (.msh / .dds / .an / .hrc).
    ///
    /// Why: the 商城 catalog lives in one folder with ~67,000 files on a spinning disk — a COLD read of a single
    /// 30 KB .msh measured ~10 ms, a 95 KB .dds ~7 ms (seek-bound, not size-bound), while a re-read of the same file
    /// (OS page cache) is ~0.16 ms. One shop card = 1 skeleton + 1-2 meshes + 1-3 textures ≈ 40 ms of BLOCKING disk
    /// I/O on the main thread, and scrolling one row rebuilt all 8 cards → a ~300 ms freeze per scroll step. That,
    /// not the decoding, was the 「捲動時服裝讀取很慢」.
    ///
    /// So: (1) every read goes through <see cref="Read"/>, which keeps the bytes in an LRU cache (scrolling back is
    /// then free), and (2) <see cref="PrefetchMesh"/> hands upcoming items to background threads that pull the mesh,
    /// read its material names and pull those textures too — by the time the card is built the main thread only pays
    /// parse + decode (CPU), never the seek.
    ///
    /// Only raw BYTES are cached, never Unity objects: byte[] is plain managed memory (GC-able, no lifetime hazard),
    /// whereas a shared Texture2D/Mesh would have to be destroyed on eviction and could still be referenced by a live
    /// avatar. Meshes are skinned in place per instance (see <see cref="SdoAvatar.AddPart"/>), so they must stay
    /// per-build anyway.
    /// </summary>
    public static class AvatarAssetCache
    {
        /// <summary>Byte-cache budget. ~96 MB ≈ a thousand garment meshes/textures (dozens of scrolled pages) and is
        /// bounded managed memory, so overshooting only costs RAM, never a bug. <see cref="Trim"/> shrinks it back when
        /// the shop closes.</summary>
        public static long BytesCapacity = 96L << 20;

        /// <summary>Background reader threads. Parallel reads on this data set measured ~3× the serial throughput
        /// (queued seeks), and they never block the frame regardless.</summary>
        public const int Workers = 3;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, byte[]> _bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _lru = new LinkedList<string>();                       // most-recent at the head
        private static readonly Dictionary<string, LinkedListNode<string>> _node = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);
        private static long _used;

        // Prefetch queue. LIFO: while the user drags the scrollbar the NEWEST request is the one about to be shown,
        // so a stale backlog must not delay it. Each entry is a mesh path (jobMesh) or a plain file (jobMesh=false).
        private static readonly List<(string Path, bool Mesh)> _queue = new List<(string, bool)>();
        private static readonly HashSet<string> _queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _liveWorkers;
        private static int _hits, _misses;

        /// <summary>Cached file read. Returns null when the file is missing/unreadable (callers already treat that as
        /// "part missing"). Safe from any thread.</summary>
        public static byte[] Read(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_lock)
            {
                if (_bytes.TryGetValue(path, out var hit)) { Touch(path); _hits++; return hit; }
            }
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch { return null; }
            lock (_lock) { _misses++; Store(path, data); }
            return data;
        }

        /// <summary>Cached text read (the <c>.an</c> frame lists). Same caching as <see cref="Read"/>.</summary>
        public static string ReadText(string path)
        {
            var b = Read(path);
            if (b == null) return null;
            try { return System.Text.Encoding.UTF8.GetString(b); }
            catch { return null; }
        }

        /// <summary>True when the file's bytes are already in RAM (a build off this path will not touch the disk).</summary>
        public static bool IsCached(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock) return _bytes.ContainsKey(path);
        }

        // ---- parsed skeleton cache -------------------------------------------------------------------------------
        // The whole 商城 builds every card off the SAME 2-4 mannequin skeletons (wshop/mshop/FEMALE/MALE.HRC); parsing
        // one per card was pure repeat work. HrcLoader output is read-only after Load (SdoAvatar only reads it), so a
        // single parsed instance is shared. A handful of entries → no eviction.
        private static readonly Dictionary<string, HrcLoader> _hrc = new Dictionary<string, HrcLoader>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Parsed skeleton for an absolute .hrc path (cached). Null when missing / not an HRC. Main thread.</summary>
        public static HrcLoader Hrc(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_hrc.TryGetValue(path, out var h)) return h;
            var b = Read(path);
            h = b != null ? HrcLoader.Load(b) : null;
            if (h != null) _hrc[path] = h;
            return h;
        }

        // ---- prefetch --------------------------------------------------------------------------------------------

        /// <summary>Queue a mesh (absolute .msh path) for background loading: its bytes, then — via the material names
        /// in its header — the textures (and texanim frame lists) the builder will ask for. No-op when already cached
        /// or queued.</summary>
        public static void PrefetchMesh(string mshPath) => Enqueue(mshPath, true);

        /// <summary>Queue a plain file (absolute path) for background loading.</summary>
        public static void PrefetchFile(string path) => Enqueue(path, false);

        private static void Enqueue(string path, bool mesh)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_lock)
            {
                if (_bytes.ContainsKey(path) && !mesh) return;     // plain file already in RAM
                if (!_queued.Add(path)) return;                    // already queued
                _queue.Add((path, mesh));
                if (_queue.Count > 512) { var drop = _queue[0]; _queue.RemoveAt(0); _queued.Remove(drop.Path); }   // oldest request is the least relevant
                Monitor.PulseAll(_lock);
            }
            EnsureWorkers();
        }

        /// <summary>Drop everything not yet started (the user scrolled away). In-flight reads finish — they are one
        /// file each, and their bytes stay cached anyway.</summary>
        public static void CancelPending()
        {
            lock (_lock) { _queue.Clear(); _queued.Clear(); }
        }

        /// <summary>Requests still waiting for a worker (diagnostics / tests).</summary>
        public static int PendingJobs { get { lock (_lock) return _queue.Count; } }

        /// <summary>Cache hit/miss counters since start (diagnostics).</summary>
        public static void Stats(out int hits, out int misses, out long bytesUsed)
        {
            lock (_lock) { hits = _hits; misses = _misses; bytesUsed = _used; }
        }

        /// <summary>Drop all cached bytes (e.g. leaving the shop for good). Parsed skeletons are kept — they are tiny
        /// and always needed.</summary>
        public static void Clear()
        {
            lock (_lock) { _bytes.Clear(); _lru.Clear(); _node.Clear(); _used = 0; _queue.Clear(); _queued.Clear(); }
        }

        /// <summary>Shrink the byte cache to <paramref name="targetBytes"/> (drops the least-recently-used first) and
        /// forget any queued prefetch. Called when the 商城 closes: the browsing working-set is worth tens of MB while
        /// scrolling, but nothing during gameplay.</summary>
        public static void Trim(long targetBytes)
        {
            lock (_lock)
            {
                _queue.Clear(); _queued.Clear();
                while (_used > targetBytes && _lru.Count > 0)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _node.Remove(last.Value);
                    if (_bytes.TryGetValue(last.Value, out var old)) { _used -= old.Length; _bytes.Remove(last.Value); }
                }
            }
        }

        private static void EnsureWorkers()
        {
            lock (_lock)
            {
                while (_liveWorkers < Workers)
                {
                    _liveWorkers++;
                    var t = new Thread(WorkerLoop) { IsBackground = true, Name = "AvatarPrefetch", Priority = System.Threading.ThreadPriority.BelowNormal };
                    t.Start();
                }
            }
        }

        // Workers idle out after a short wait rather than living forever: an editor domain reload resets the statics
        // and any surviving thread would be orphaned. They restart on the next Enqueue.
        private static void WorkerLoop()
        {
            try
            {
                while (true)
                {
                    (string Path, bool Mesh) job;
                    lock (_lock)
                    {
                        while (_queue.Count == 0)
                            if (!Monitor.Wait(_lock, 5000)) { _liveWorkers--; return; }
                        job = _queue[_queue.Count - 1];            // LIFO: newest scroll position first
                        _queue.RemoveAt(_queue.Count - 1);
                        _queued.Remove(job.Path);
                    }
                    try { if (job.Mesh) LoadMeshFamily(job.Path); else Read(job.Path); }
                    catch { /* prefetch is best-effort; the main-thread build re-reads and reports properly */ }
                }
            }
            catch { lock (_lock) _liveWorkers--; }
        }

        // Pull a mesh + everything the builder will resolve from it. ReadMaterialNames / FindDdsPath are pure byte and
        // path work (no Unity API) so they are safe off the main thread; FindDdsPath's folder index is lock-guarded.
        private static void LoadMeshFamily(string mshPath)
        {
            var bytes = Read(mshPath);
            if (bytes == null) return;
            string dir = Path.GetDirectoryName(mshPath);
            if (string.IsNullOrEmpty(dir)) return;
            string stem = Path.GetFileNameWithoutExtension(mshPath);
            List<string> names;
            try { names = MshLoader.ReadMaterialNames(bytes); }
            catch { return; }
            names.Add(stem + ".dds");                              // the builder's own-id fallback (MeshSelfDds)
            foreach (var nm in names)
            {
                if (string.IsNullOrEmpty(nm)) continue;
                if (TexAnimEx.TryParse(nm, out var spec))          // 換幀貼圖:先抓幀清單,再抓每一幀
                {
                    string an = Path.Combine(dir, spec.Name + ".an");
                    var txt = ReadText(an);
                    if (txt == null) continue;
                    foreach (var fn in TexAnimEx.ParseAn(txt))
                    {
                        var fp = SdoAvatarBuilder.FindDdsPath(dir, fn);
                        if (fp != null) Read(fp);
                    }
                    continue;
                }
                var p = SdoAvatarBuilder.FindDdsPath(dir, nm);
                if (p != null) Read(p);
            }
        }

        // ---- LRU bookkeeping (caller holds _lock) ------------------------------------------------------------------

        private static void Store(string path, byte[] data)
        {
            if (data == null) return;
            if (_bytes.ContainsKey(path)) { Touch(path); return; }
            _bytes[path] = data;
            _node[path] = _lru.AddFirst(path);
            _used += data.Length;
            while (_used > BytesCapacity && _lru.Count > 1)
            {
                var last = _lru.Last;
                if (last == null) break;
                _lru.RemoveLast();
                _node.Remove(last.Value);
                if (_bytes.TryGetValue(last.Value, out var old)) { _used -= old.Length; _bytes.Remove(last.Value); }
            }
        }

        private static void Touch(string path)
        {
            if (!_node.TryGetValue(path, out var n)) return;
            _lru.Remove(n);
            _lru.AddFirst(n);
        }
    }
}
