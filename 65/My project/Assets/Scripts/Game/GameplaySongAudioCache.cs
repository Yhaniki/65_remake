using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sdo.Game
{
    /// <summary>
    /// 正式遊玩的 mp3 解碼快取＋預先解碼 —— 讓外部歌(osu/StepMania/.gn 包，音檔常是 mp3)進遊戲不用每次
    /// 卡著整首解一秒多。
    ///
    /// 為什麼會慢:官方歌是 .ogg，Unity 原生解幾乎不用等;但外部歌音檔常是 .mp3，桌面版 Unity 不解 mp3，
    /// 得用內建 NLayer 託管解碼器**整首解完**才能播(2~3 分鐘的曲子 ≈1.4 秒)。這一秒就卡在「按下 Start →
    /// 進得去」之間 —— gameplay 的載入畫面等 _audioReady，而 _audioReady 正是等這首整段解完
    /// (見 ScreenGameplay.LoadAndPlayAudio / LocalBootReady)。
    ///
    /// 解法(跟 <see cref="EditorAudioCache"/> 同一招，只是服務對象換成正式遊玩):
    ///   • 預抓:玩家在選歌畫面按下「確認」的當下(SongSelectScreen.OnConfirm)就背景開解，等房主真的
    ///     按 Start，PCM 通常已經在手上 → 秒進。從 confirm 回房間到按 Start 通常 &gt; 1.4s，足夠解完;
    ///     就算沒解完，進遊戲拿到的是**同一個正在跑的 Task**(不是再解一次)，等於把解碼起點提前，淨賺。
    ///   • 快取:解過的留著，retry 同一首、或選了又反悔選回來，都 0 等待。
    ///
    /// 代價是記憶體:PCM 是 float，一首 2.5 分鐘立體聲 ≈ 53 MB，所以只留 <see cref="Capacity"/> 首，滿了丟最舊的。
    ///
    /// 與 <see cref="EditorAudioCache"/> 的差別:那個是譜面編輯器 Q/E 換歌用的(留前後鄰居，容量 3)，這個是
    /// 正式遊玩用的(留當前＋剛玩過，容量 2 —— 遊玩時場上還有 3D 場景/舞者吃記憶體，壓更保守)。兩者共用底層
    /// <see cref="Mp3Decoder.Decode"/>，但生命週期/容量不同，所以各自一份 static。
    ///
    /// sync 一定要用 <see cref="ScreenGameplay.Mp3SyncFor"/> 算(和 LoadAndPlayAudio 進場時同一個函式)，不然
    /// 預抓出來的 PCM 位置跟實際播的不一樣。<see cref="Key"/> 也含 sync，同一檔用不同 sync 各留一份(不會串)。
    ///
    /// 執行緒:解碼在 worker thread(<see cref="Mp3Decoder.Decode"/> 不碰 Unity API);這個類別的方法只在
    /// 主執行緒(選歌確認、進遊戲協程)被呼叫，所以字典不用鎖。
    /// </summary>
    public static class GameplaySongAudioCache
    {
        /// <summary>同時留幾首的 PCM。2 = 現在這首 + 上一首(retry / 選了又反悔選回來 都命中)。</summary>
        public const int Capacity = 2;

        private static readonly Dictionary<string, Task<Mp3Pcm>> _byKey =
            new Dictionary<string, Task<Mp3Pcm>>(StringComparer.Ordinal);
        private static readonly List<string> _order = new List<string>();   // 最舊的在前面

        /// <summary>快取的 key:同一個檔用不同 <paramref name="sync"/> 解出來的 PCM 位置不同，要分開存。純函式 —— 有單元測試。</summary>
        public static string Key(string path, Mp3Decoder.Mp3Sync sync)
            => (path ?? "") + "\0" + (int)sync;

        /// <summary>取得(必要時啟動)這個檔的解碼工作。已經解好(或正在解)的回傳同一個 Task —— 呼叫端的
        /// <c>while (!task.IsCompleted) yield return null;</c> 命中預抓時一幀都不會等。簽章對齊
        /// <see cref="ScreenGameplay.mp3Decoder"/> 委派。</summary>
        public static Task<Mp3Pcm> Get(string path, Mp3Decoder.Mp3Sync sync)
        {
            if (string.IsNullOrEmpty(path)) return Task.FromResult<Mp3Pcm>(null);
            string key = Key(path, sync);
            if (_byKey.TryGetValue(key, out var hit))
            {
                Touch(key);
                if (!hit.IsFaulted && !hit.IsCanceled) return hit;
                Remove(key);   // 上次解爆了 → 不要一直回傳同一個壞結果
            }
            var task = Task.Run(() => Mp3Decoder.Decode(path, sync));
            _byKey[key] = task;
            _order.Add(key);
            Trim();
            return task;
        }

        /// <summary>背景先解好這首(不等結果)。已在快取裡、或不是 mp3(ogg/wav 由 Unity 原生解，本來就快，
        /// 占著幾十 MB 不划算)就跳過。看**內容**判斷是不是 mp3，不看副檔名(那幾個「叫 .mp3 的 Ogg」會被
        /// 白解一次還解出空的，見 <see cref="Sdo.Osu.AudioFileType"/>)。</summary>
        public static void Prefetch(string path, Mp3Decoder.Mp3Sync sync)
        {
            if (string.IsNullOrEmpty(path) || _byKey.ContainsKey(Key(path, sync))) return;
            if (Sdo.Osu.AudioFileType.Of(path) != Sdo.Osu.AudioKind.Mp3) return;
            Get(path, sync);
        }

        /// <summary>清掉整個快取(回大廳等釋放記憶體時叫，把上百 MB 的 PCM 還回去)。</summary>
        public static void Clear()
        {
            _byKey.Clear();
            _order.Clear();
        }

        /// <summary>目前留著幾首(測試/狀態用)。</summary>
        public static int Count => _byKey.Count;

        private static void Touch(string key)
        {
            _order.Remove(key);
            _order.Add(key);
        }

        private static void Remove(string key)
        {
            _byKey.Remove(key);
            _order.Remove(key);
        }

        private static void Trim()
        {
            while (_order.Count > Capacity)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _byKey.Remove(oldest);
            }
        }
    }
}
