namespace Sdo.Osu
{
    /// <summary>A single mania note. Tap has no end time; Hold carries an end time.</summary>
    public readonly struct OsuHitObject
    {
        /// <summary>Lane index, 0-based (0..keys-1).</summary>
        public int Lane { get; }

        /// <summary>Judgment time of the note (head time for holds), in milliseconds.</summary>
        public int StartTimeMs { get; }

        /// <summary>End time for hold notes, in milliseconds; null for taps.</summary>
        public int? EndTimeMs { get; }

        public bool IsHold => EndTimeMs.HasValue;

        /// <summary>
        /// SDO online/NX 炸彈 (StepFile note_type 1 on a lane): a note to AVOID, not hit. It is never a hold, never
        /// judged as a tap; stepping on it (lane held as it reaches the judge line) detonates it. false for normal notes.
        /// </summary>
        public bool IsBomb { get; }

        /// <summary>
        /// 「看得到但打不到」的裝飾音符 —— StepMania 負 BPM (warp) 掃過去的那一段譜。播放頭在 warp 那一瞬間直接
        /// 從 warp 起拍跳到結束拍,中間這些音符連一幀判定機會都沒有,所以它們**不判定、不 miss、不進滿分分母**
        /// (<see cref="OsuBeatmap.TotalNotes"/>),只是照 <see cref="ScrollTimeMs"/> 捲過畫面然後消失。
        /// StepMania 3.9 其實還是把它們算進 note 總數(打不到卻算分母,等於白扣),這裡刻意不算 —— 見 <see cref="SmChart"/>。
        /// </summary>
        public bool IsFake { get; }

        /// <summary>
        /// **顯示用**時間 (ms, 可含小數):音符捲到判定線的時刻,預設 == <see cref="StartTimeMs"/>。
        /// 只有 StepMania warp 會讓它和判定時間分家 —— warp 是「零秒內跳過一段拍子」,那段拍子在時間軸上沒有厚度,
        /// 但在**畫面上**仍然要照拍子鋪開(StepMania 3.9 的 note 位置是 beat spacing,見 ArrowEffects::ArrowGetYOffset),
        /// 所以 <see cref="SmChart"/> 把 warp 壓成一個極短(1ms)的超高速捲動窗,warp 內的音符就分佈在那一窗裡:
        /// 進場時照拍子一顆顆排好往下捲,播放頭掃過那 1ms 時整批瞬間刷過判定線 —— 和 StepMania 看起來一樣。
        /// </summary>
        public double ScrollTimeMs { get; }

        /// <summary>顯示用的長條尾端時間 (ms);tap 時 == <see cref="ScrollTimeMs"/>。理由同 <see cref="ScrollTimeMs"/>。</summary>
        public double ScrollEndTimeMs { get; }

        /// <param name="scrollTimeMs">顯示用時間;null = 跟判定時間一樣(.gn/.osu/沒有 warp 的 .sm 都走這條)。</param>
        /// <param name="scrollEndTimeMs">顯示用尾端時間;null = 長條取 <paramref name="endTimeMs"/>、tap 取頭部顯示時間。
        /// 有給 <paramref name="scrollTimeMs"/> 的長條請把這個也一起給,不然尾端會落回判定時間。</param>
        public OsuHitObject(int lane, int startTimeMs, int? endTimeMs = null, bool isBomb = false,
            bool isFake = false, double? scrollTimeMs = null, double? scrollEndTimeMs = null)
        {
            Lane = lane;
            StartTimeMs = startTimeMs;
            EndTimeMs = endTimeMs;
            IsBomb = isBomb;
            IsFake = isFake;
            ScrollTimeMs = scrollTimeMs ?? startTimeMs;
            ScrollEndTimeMs = scrollEndTimeMs
                ?? (endTimeMs.HasValue && !scrollTimeMs.HasValue ? endTimeMs.Value : ScrollTimeMs);
        }
    }
}
