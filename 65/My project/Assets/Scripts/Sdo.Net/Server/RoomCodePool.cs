using System;
using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>
    /// 5 位數房號的配發池(<see cref="NetLimits.MinRoomCode"/>..<see cref="NetLimits.MaxRoomCode"/>)。
    ///
    /// **為什麼是池而不是「隨機抽 + 撞了重試」**:重試法在房間數多的時候會變慢(生日問題)，
    /// 而且最糟情況沒有上界 —— 池子是 O(1) 配發、O(1) 回收，而且天然保證不重複。
    /// 90000 個 int 的 queue 約 360 KB，對 server 來說不值一提。
    ///
    /// **回收後不立刻重發**:用 FIFO 而不是 LIFO。剛關掉的房號若馬上被下一間房拿走，
    /// 手上還留著舊房號的玩家會**進到一間完全陌生的房**。走 queue 尾端的話，
    /// 要等到把 90000 個號碼發完一輪才會重用同一個號。
    ///
    /// 純資料結構,零 IO、零 UnityEngine。亂數 seed 由外部注入，測試才能重現。
    /// </summary>
    public sealed class RoomCodePool
    {
        private readonly Queue<int> _free;

        /// <summary>index = code - MinRoomCode。用來擋重複 <see cref="Return"/> ——
        /// 沒有這道檢查，同一個號碼被歸還兩次就會在池子裡出現兩份，然後被配給兩間不同的房。</summary>
        private readonly bool[] _inPool;

        /// <summary>總共有多少個房號。</summary>
        public int Capacity { get; }

        /// <summary>還剩幾個可配發。</summary>
        public int Available => _free.Count;

        /// <summary>目前配出去幾個。</summary>
        public int Rented => Capacity - _free.Count;

        /// <param name="seed">洗牌用的亂數種子。測試傳固定值以取得可重現的順序。</param>
        public RoomCodePool(int seed = 0)
        {
            Capacity = NetLimits.MaxRoomCode - NetLimits.MinRoomCode + 1;
            _inPool = new bool[Capacity];

            // 先攤平再 Fisher-Yates 洗牌 —— 房號要看起來是隨意的(否則第一間房永遠是 10000，
            // 隨便猜就猜中別人的房)。
            var all = new int[Capacity];
            for (int i = 0; i < Capacity; i++) all[i] = NetLimits.MinRoomCode + i;

            var rng = new Random(seed);
            for (int i = Capacity - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = all[i]; all[i] = all[j]; all[j] = t;
            }

            _free = new Queue<int>(Capacity);
            for (int i = 0; i < Capacity; i++)
            {
                _free.Enqueue(all[i]);
                _inPool[all[i] - NetLimits.MinRoomCode] = true;
            }
        }

        /// <summary>這個數字落在合法房號範圍內嗎?</summary>
        public static bool IsValidCode(int code)
            => code >= NetLimits.MinRoomCode && code <= NetLimits.MaxRoomCode;

        /// <summary>配一個房號。池子空了(= 房間數達到 90000)回 false。</summary>
        public bool TryRent(out int code)
        {
            if (_free.Count == 0) { code = 0; return false; }
            code = _free.Dequeue();
            _inPool[code - NetLimits.MinRoomCode] = false;
            return true;
        }

        /// <summary>
        /// 歸還房號(關房時呼叫)。
        /// 回 false = 這個號碼不合法，或它本來就已經在池子裡(重複歸還)—— 兩種都不會改變池子。
        /// </summary>
        public bool Return(int code)
        {
            if (!IsValidCode(code)) return false;
            int idx = code - NetLimits.MinRoomCode;
            if (_inPool[idx]) return false;   // 重複歸還:忽略，不要讓池子出現重複

            _inPool[idx] = true;
            _free.Enqueue(code);
            return true;
        }

        /// <summary>這個房號目前被配出去了嗎?</summary>
        public bool IsRented(int code)
            => IsValidCode(code) && !_inPool[code - NetLimits.MinRoomCode];
    }
}
