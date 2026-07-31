using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sdo.Game
{
    /// <summary>
    /// 譜面編輯器專用的 mp3 解碼快取＋預先解碼 —— 讓 Q/E 換歌不用每次等一秒多。
    ///
    /// 為什麼會慢：官方歌是 .ogg，Unity 原生解碼幾乎不用等；但匯進來的歌曲包常常整包是 .mp3
    /// （[NX] 那包 232 首全是），而桌面版 Unity 不解 mp3，得用內建的 NLayer 託管解碼器**整首解完**才能播。
    /// 實測那包平均 <b>1.1 秒／首</b>（2~3 分鐘的曲子）。一首一首校 offset 的時候，這一秒就卡在每一次 Q/E 上。
    ///
    /// 兩件事解掉它：
    ///   • <b>快取</b>：解過的留著。校時最常見的動作是「E 到下一首、覺得不對 Q 回來再聽」，回頭那次就 0 等待。
    ///   • <b>預抓</b>：載完一首之後，背景先把上一首／下一首解好。等你真的按下 E，PCM 已經在手上了。
    ///
    /// 代價是記憶體：PCM 是 float，一首 2.5 分鐘的立體聲 ≈ 53 MB，所以只留 <see cref="Capacity"/> 首
    /// （現在這首＋前後各一首），滿了就丟最舊的。這是編輯器工具限定的取捨，正式遊玩不走這條路。
    ///
    /// 執行緒：解碼本身在 worker thread（<see cref="Mp3Decoder.Decode"/> 不碰 Unity API）；這個類別的方法
    /// 只在主執行緒（協程）被呼叫，所以字典不用鎖。
    /// </summary>
    public static class EditorAudioCache
    {
        /// <summary>同時留幾首的 PCM。3 = 現在這首 + 前一首 + 下一首。</summary>
        public const int Capacity = 3;

        private static readonly Dictionary<string, Task<Mp3Pcm>> _byPath =
            new Dictionary<string, Task<Mp3Pcm>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _order = new List<string>();   // 最舊的在前面

        /// <summary>取得（必要時啟動）這個檔的解碼工作。已經解好的回傳一個「已完成」的 Task，
        /// 呼叫端的 <c>while (!task.IsCompleted) yield return null;</c> 就一幀都不會等。</summary>
        public static Task<Mp3Pcm> Get(string path, Mp3Decoder.Mp3Sync sync)
        {
            if (string.IsNullOrEmpty(path)) return Task.FromResult<Mp3Pcm>(null);
            if (_byPath.TryGetValue(path, out var hit))
            {
                Touch(path);
                if (!hit.IsFaulted && !hit.IsCanceled) return hit;
                Remove(path);   // 上次解爆了 → 不要一直回傳同一個壞結果
            }
            var task = Task.Run(() => Mp3Decoder.Decode(path, sync));
            _byPath[path] = task;
            _order.Add(path);
            Trim();
            return task;
        }

        /// <summary>背景先解好這首（不等結果）。已在快取裡就什麼都不做。</summary>
        public static void Prefetch(string path, Mp3Decoder.Mp3Sync sync)
        {
            if (string.IsNullOrEmpty(path) || _byPath.ContainsKey(path)) return;
            // ogg/wav 由 Unity 原生解，本來就快，不必占著記憶體。看內容判斷（副檔名會騙人，見 AudioFileType）——
            // 不然那 4 個「叫 .mp3 的 Ogg」會被白解一次，還解出空的。
            if (Sdo.Osu.AudioFileType.Of(path) != Sdo.Osu.AudioKind.Mp3) return;
            Get(path, sync);
        }

        /// <summary>清掉整個快取（離開編輯器時叫，把幾百 MB 的 PCM 還回去）。</summary>
        public static void Clear()
        {
            _byPath.Clear();
            _order.Clear();
        }

        /// <summary>目前留著幾首（測試/狀態列用）。</summary>
        public static int Count => _byPath.Count;

        private static void Touch(string path)
        {
            _order.Remove(path);
            _order.Add(path);
        }

        private static void Remove(string path)
        {
            _byPath.Remove(path);
            _order.Remove(path);
        }

        private static void Trim()
        {
            while (_order.Count > Capacity)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _byPath.Remove(oldest);
            }
        }
    }
}
