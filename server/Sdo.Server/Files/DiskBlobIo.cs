using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Sdo.Net;
using Sdo.Net.Server;
using Sdo.Osu;

namespace Sdo.Server.Files
{
    /// <summary>一個內容包的完整紀錄(<c>packs/&lt;hex&gt;.json</c> 的內容)。歌曲或 MMD 模型,見 <see cref="Kind"/>。</summary>
    public sealed class BlobPack
    {
        public string PackId;
        public long LastUsedUtcMs;

        /// <summary>
        /// <c>NetProto.BlobKindSong</c> 或 <c>BlobKindModel</c>。倉庫本身是內容尋址的、兩種共用,
        /// 但**收檔時要套的白名單不同**,所以每個包記著自己是哪一種:同一個 packId 用另一種 kind
        /// 再上傳一次會被拒(<c>BlobErrKindMismatch</c>)。
        ///
        /// 舊檔沒有這個欄位 → 讀回來是空字串,一律當成 song(這個功能出現之前所有的包都是歌)。
        /// </summary>
        public string Kind = "";

        public List<PackFileEntry> Files = new List<PackFileEntry>();

        public long TotalBytes
        {
            get
            {
                long n = 0;
                for (int i = 0; i < Files.Count; i++) n += Files[i].Length;
                return n;
            }
        }
    }

    /// <summary>
    /// blob 倉庫的磁碟層 —— <see cref="BlobIndex"/> 決定「刪什麼」,這裡負責真的去碰檔案。
    ///
    /// 佈局(<c>&lt;dataDir&gt;/blobs/</c>):
    /// <code>
    ///   files/&lt;sha[0:2]&gt;/&lt;sha&gt;      檔案本體,內容尋址 → 天然去重
    ///   packs/&lt;packId hex&gt;.json      這首歌由哪些檔案組成 + 最後使用時間
    ///   tmp/&lt;uploadId&gt;/              上傳暫存,收完驗過才原子搬進 files/
    /// </code>
    ///
    /// 🔴 <b>pack 的檔名要去掉 <c>sha256:</c> 前綴</b>。Windows 上冒號不是「不合法字元然後報錯」而是
    /// **NTFS alternate data stream 的分隔符** —— <c>packs/sha256:abcd.json</c> 會安靜地把資料寫成
    /// <c>packs/sha256</c> 這個檔案的隱形附屬串流:寫入成功、<c>Directory.GetFiles</c> 看不到、
    /// 重開 server 之後所有包都「消失」了。開發時 server 跑在 Windows 上,所以這不是理論問題。
    /// </summary>
    public sealed class DiskBlobIo
    {
        private readonly string _root;

        public DiskBlobIo(string blobDir)
        {
            _root = blobDir;
            Directory.CreateDirectory(FilesDir);
            Directory.CreateDirectory(PacksDir);
            Directory.CreateDirectory(TmpDir);
        }

        public string FilesDir => Path.Combine(_root, "files");
        public string PacksDir => Path.Combine(_root, "packs");
        public string TmpDir => Path.Combine(_root, "tmp");

        // ---- 路徑 ----

        /// <summary>packId → 檔名用的 hex(去掉 <c>sha256:</c>,見類別註解)。不合法 → null。</summary>
        public static string PackFileName(string packId)
        {
            if (!SongPackId.IsWellFormed(packId)) return null;
            return packId.Substring(SongPackId.Prefix.Length);
        }

        public string PackPath(string packId)
        {
            var name = PackFileName(packId);
            return name == null ? null : Path.Combine(PacksDir, name + ".json");
        }

        /// <summary>
        /// sha → 本體檔案的絕對路徑,**回傳磁碟上實際存在的那一個**;都不在回 null。
        /// sha 不是 64 字小寫 hex 也回 null(**擋路徑注入**)。
        ///
        /// 一份本體有兩種可能的長相:<c>&lt;sha&gt;</c>(原始)或 <c>&lt;sha&gt;.z</c>(deflate,見
        /// <see cref="PackCompression"/>)。新收的一律存壓縮版 —— 磁碟省下來的同時,
        /// 下載時可以**直接把位元組送出去**,server 完全不必壓也不必解。
        /// 副檔名區分讓這件事天然向後相容:壓縮功能出現之前存下來的本體是原始的,照樣讀得到。
        /// </summary>
        public string BlobPath(string sha)
        {
            var raw = RawBlobPath(sha);
            if (raw == null) return null;
            if (File.Exists(raw)) return raw;
            var z = raw + PackCompression.Extension;
            return File.Exists(z) ? z : null;
        }

        /// <summary>這個 sha 的本體「該」寫在哪(不管在不在)。<paramref name="compressed"/> 決定副檔名。</summary>
        public string TargetBlobPath(string sha, bool compressed)
        {
            var raw = RawBlobPath(sha);
            if (raw == null) return null;
            return compressed ? raw + PackCompression.Extension : raw;
        }

        /// <summary>這個 sha 的本體是壓縮版嗎(不存在也回 false)。</summary>
        public bool IsBlobCompressed(string sha)
        {
            var p = BlobPath(sha);
            return p != null && p.EndsWith(PackCompression.Extension, StringComparison.Ordinal);
        }

        private string RawBlobPath(string sha)
        {
            if (!IsSha256(sha)) return null;
            return Path.Combine(FilesDir, sha.Substring(0, 2), sha);
        }

        /// <summary>只認 64 字小寫 hex。收 client 送來的 sha 一定要先過這關 —— 它會直接變成檔名。</summary>
        public static bool IsSha256(string sha)
        {
            if (string.IsNullOrEmpty(sha) || sha.Length != 64) return false;
            for (int i = 0; i < 64; i++)
            {
                char c = sha[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        // ---- 檔案本體 ----

        public bool HasBlob(string sha) => BlobPath(sha) != null;   // BlobPath 只回存在的那一個

        public long BlobLength(string sha)
        {
            var p = BlobPath(sha);
            if (p == null) return -1;
            try { return new FileInfo(p).Length; } catch { return -1; }
        }

        public byte[] ReadBlob(string sha)
        {
            var p = BlobPath(sha);
            if (p == null) return null;
            try { return File.ReadAllBytes(p); } catch { return null; }
        }

        /// <summary>
        /// 把暫存檔收進倉庫。**會自己重算 SHA-256 驗證**(絕不信上傳者宣稱的 hash),
        /// 對不上就不收 → 回 false 並刪掉暫存。
        /// </summary>
        public bool CommitBlob(string tmpPath, string expectSha) => Commit(tmpPath, expectSha, compressed: false);

        /// <summary>
        /// 收一個**壓縮過**的暫存檔(<c>.z</c>)。同樣絕不信上傳者:先解壓到旁邊、重算 SHA-256,
        /// 對不上就整個丟掉;對得上才把**壓縮版**收進倉庫(解壓出來的那份用完就刪)。
        ///
        /// 存壓縮版是刻意的:磁碟省 9 倍,而且之後每一次下載都可以把位元組原封不動送出去 ——
        /// server 是單執行緒的 actor loop,在那裡面壓縮幾百 MB 會把所有房間一起卡住。
        /// </summary>
        public bool CommitCompressedBlob(string zTmpPath, string expectSha)
        {
            if (!IsSha256(expectSha) || !File.Exists(zTmpPath)) return false;

            var rawTmp = zTmpPath + ".raw";
            if (!PackCompression.DecompressFile(zTmpPath, rawTmp))
            {
                TryDelete(rawTmp);
                TryDelete(zTmpPath);
                return false;
            }

            string actual = HashFile(rawTmp);
            TryDelete(rawTmp);   // 驗完就不需要了 —— 倉庫存的是壓縮版
            if (!string.Equals(actual, expectSha, StringComparison.Ordinal))
            {
                TryDelete(zTmpPath);
                return false;
            }
            return Place(zTmpPath, expectSha, compressed: true);
        }

        private bool Commit(string tmpPath, string expectSha, bool compressed)
        {
            if (!IsSha256(expectSha) || !File.Exists(tmpPath)) return false;

            string actual = HashFile(tmpPath);
            if (!string.Equals(actual, expectSha, StringComparison.Ordinal))
            {
                TryDelete(tmpPath);
                return false;
            }
            return Place(tmpPath, expectSha, compressed);
        }

        private bool Place(string tmpPath, string expectSha, bool compressed)
        {
            // 已經有這個本體了(去重命中)—— 不管在磁碟上是哪一種長相,內容都是同一份。
            if (HasBlob(expectSha)) { TryDelete(tmpPath); return true; }

            var dst = TargetBlobPath(expectSha, compressed);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(dst)) { TryDelete(tmpPath); return true; }   // 已經有了(去重命中)
                File.Move(tmpPath, dst);
                return true;
            }
            catch
            {
                // 同一個檔被兩條連線同時上傳時 Move 會撞名。已經在了就算成功。
                if (File.Exists(dst)) { TryDelete(tmpPath); return true; }
                TryDelete(tmpPath);
                return false;
            }
        }

        public IEnumerable<string> ListBlobShas()
        {
            string[] shards;
            try { shards = Directory.GetDirectories(FilesDir); } catch { yield break; }
            for (int i = 0; i < shards.Length; i++)
            {
                string[] files;
                try { files = Directory.GetFiles(shards[i]); } catch { continue; }
                for (int j = 0; j < files.Length; j++)
                {
                    var name = Path.GetFileName(files[j]);
                    // 壓縮版的本體叫 <sha>.z —— 還原成 sha 才認得出來。
                    // 漏掉這一步的話 janitor 會覺得倉庫是空的:清理計畫把每個包都判成「缺檔」,
                    // 而磁碟用量永遠算成 0(見 UsedBytes)。
                    if (name.EndsWith(PackCompression.Extension, StringComparison.Ordinal))
                        name = name.Substring(0, name.Length - PackCompression.Extension.Length);
                    if (IsSha256(name)) yield return name;
                }
            }
        }

        public bool DeleteBlob(string sha)
        {
            var p = BlobPath(sha);
            return p != null && TryDelete(p);
        }

        // ---- 上傳暫存 ----

        /// <summary>開一個上傳用的暫存資料夾。<paramref name="uploadId"/> 由 server 自己產(不吃 client 的字串)。</summary>
        public string NewTempDir(long uploadId)
        {
            var d = Path.Combine(TmpDir, uploadId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(d);
            return d;
        }

        public void DropTempDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        /// <summary>
        /// 開機時清掉 <c>tmp/</c> 裡的殘骸 —— 上一次執行中途被 kill 掉留下的半個上傳。
        /// 不清的話那些位元組永遠算在磁碟用量裡而且沒有任何東西引用它們。
        /// </summary>
        public int ClearTemp()
        {
            int n = 0;
            string[] dirs;
            try { dirs = Directory.GetDirectories(TmpDir); } catch { return 0; }
            for (int i = 0; i < dirs.Length; i++)
            {
                try { Directory.Delete(dirs[i], true); n++; } catch { }
            }
            return n;
        }

        // ---- pack 紀錄 ----

        public bool HasPack(string packId)
        {
            var p = PackPath(packId);
            return p != null && File.Exists(p);
        }

        public BlobPack LoadPack(string packId)
        {
            var p = PackPath(packId);
            if (p == null || !File.Exists(p)) return null;
            string json;
            try { json = File.ReadAllText(p, Encoding.UTF8); } catch { return null; }

            object node;
            if (!NetJson.TryParse(json, out node)) return null;

            var pack = new BlobPack
            {
                PackId = NetJson.Str(node, "packId", packId),
                LastUsedUtcMs = NetJson.Long(node, "lastUsedUtcMs"),
                Kind = NetJson.Str(node, "kind", ""),   // 舊檔沒有 → 空 = song(見 BlobPack.Kind)
            };
            var arr = NetJson.Arr(node, "files");
            for (int i = 0; i < arr.Count; i++)
            {
                var f = arr[i];
                pack.Files.Add(new PackFileEntry(
                    NetJson.Str(f, "path"),
                    NetJson.Long(f, "len"),
                    NetJson.Str(f, "sha256")));
            }
            return pack.Files.Count > 0 ? pack : null;
        }

        public bool SavePack(BlobPack pack)
        {
            if (pack == null) return false;
            var p = PackPath(pack.PackId);
            if (p == null) return false;

            var files = JArr.New();
            for (int i = 0; i < pack.Files.Count; i++)
            {
                var f = pack.Files[i];
                files.Add(JObj.New().Str("path", f.RelPath).Long("len", f.Length).Str("sha256", f.Sha256 ?? ""));
            }
            var json = JObj.New()
                .Str("packId", pack.PackId)
                .Str("kind", pack.Kind ?? "")
                .Long("lastUsedUtcMs", pack.LastUsedUtcMs)
                .Long("sizeTotal", pack.TotalBytes)
                .Put("files", files)
                .Json();

            try
            {
                // 先寫暫存再換過去 —— 寫一半被 kill 的話留下的是舊的完整檔,而不是壞掉的 json。
                var tmp = p + ".new";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
                return true;
            }
            catch { return false; }
        }

        /// <summary>更新「最後用到」的時間戳。upload / download / query 命中都要叫它(見 <see cref="BlobPackRecord"/>)。</summary>
        public void Touch(string packId, long nowUtcMs)
        {
            var pack = LoadPack(packId);
            if (pack == null) return;
            pack.LastUsedUtcMs = nowUtcMs;
            SavePack(pack);
        }

        public bool DeletePack(string packId)
        {
            var p = PackPath(packId);
            return p != null && TryDelete(p);
        }

        /// <summary>給 <see cref="BlobIndex.Plan"/> 吃的摘要清單。</summary>
        public List<BlobPackRecord> ListPackRecords()
        {
            var list = new List<BlobPackRecord>();
            string[] files;
            try { files = Directory.GetFiles(PacksDir, "*.json"); } catch { return list; }

            for (int i = 0; i < files.Length; i++)
            {
                var hex = Path.GetFileNameWithoutExtension(files[i]);
                var pack = LoadPack(SongPackId.Prefix + hex);
                if (pack == null) continue;

                var shas = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int j = 0; j < pack.Files.Count; j++)
                {
                    var sha = pack.Files[j].Sha256;
                    if (IsSha256(sha) && seen.Add(sha)) shas.Add(sha);
                }

                list.Add(new BlobPackRecord
                {
                    PackId = pack.PackId,
                    LastUsedUtcMs = pack.LastUsedUtcMs,
                    Shas = shas.ToArray(),
                    TotalBytes = pack.TotalBytes,
                });
            }
            return list;
        }

        /// <summary><c>files/</c> 目前佔用的位元組(去重後的真實磁碟用量)。</summary>
        public long UsedBytes()
        {
            long n = 0;
            foreach (var sha in ListBlobShas())
            {
                long len = BlobLength(sha);
                if (len > 0) n += len;
            }
            return n;
        }

        // ---- 小工具 ----

        public static string HashFile(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var h = sha.ComputeHash(fs);
                    var sb = new StringBuilder(64);
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        private static bool TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; }
            catch { return false; }
        }
    }
}
