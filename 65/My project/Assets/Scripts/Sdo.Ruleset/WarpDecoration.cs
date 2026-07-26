using Sdo.Osu;

namespace Sdo.Ruleset
{
    /// <summary>
    /// warp(StepMania 負 BPM)掃掉的那批裝飾音(<see cref="OsuHitObject.IsFake"/>)在「歌曲變速」**關閉**時
    /// 怎麼處理 —— 純顯示決策,**一個字都不碰判定**。
    ///
    /// 「歌曲變速」關 = <c>ScreenGameplay.constantScroll</c> = osu 的 Constant Speed:
    /// <see cref="ManiaScroll.Build"/> 在這個模式下把**所有** timing point 丟掉(<c>pts = null</c>),
    /// 連 <see cref="SmChart"/> 給每個 warp 補的那個 1ms 超高速顯示窗也一起沒了。那個窗正是「整段被跳過的拍子
    /// 照拍距鋪開、再瞬間刷過判定線」的唯一機制(見 docs/architecture/sm-negative-bpm-warp.md §3),
    /// 沒有它,warp 內幾十上百顆音符的 <see cref="OsuHitObject.ScrollTimeMs"/> 相差不到 1ms —— 線性捲動下
    /// 它們全部疊在同一個位置一起捲進來,畫面上是一坨互相重疊、打不到的音符。
    ///
    /// 官方(StepMania 3.9)沒有這個模式,所以沒有「正確的畫法」可抄:這裡選擇**不畫**。
    /// 判定路徑完全不受影響 —— <see cref="OsuHitObject.IsFake"/> 本來就不判定/不 miss/不進滿分分母,
    /// warp 炸彈的「按住穿過 = 自動打擊」(<c>TickBombs</c> → <c>WarpMineStep</c>)照跑,
    /// 所以關掉變速不會改變任何一分,只是那批裝飾音看不到。
    /// </summary>
    public static class WarpDecoration
    {
        /// <summary>這顆音符這一幀該不該被藏起來(不畫)。
        /// <paramref name="editorMode"/> 為 true 時一律不藏:譜面編輯器要看得到譜上所有東西。</summary>
        public static bool IsHidden(OsuHitObject note, bool constantScroll, bool editorMode = false)
        {
            return note.IsFake && constantScroll && !editorMode;
        }

        /// <summary>被 <see cref="IsHidden"/> 藏起來的音符什麼時候可以從存活視窗收掉(退場)。
        ///
        /// 藏起來的音符走不到「流出畫面才收」那條路徑,而 <c>TickBombs</c> 又把 <see cref="OsuHitObject.IsFake"/>
        /// 的退場整個讓給顯示端 —— 沒有這條,那批音符會永遠卡在存活游標前面,每一幀都被重掃一次。
        ///
        /// 判定時刻過了就能收,**炸彈例外**:warp 炸彈是「按住穿過 warp」gimmick 的觸發器,得等跨線游標
        /// <paramref name="bombCursorMs"/> 真的越過它之後才收。顯示端跑在 <c>TickBombs</c> 前面,先收掉的話
        /// 同一幀的跨線偵測只會看到 Done 而整批跳過,gimmick 永遠不會發生。</summary>
        public static bool CanRetire(OsuHitObject note, double nowMs, double bombCursorMs)
        {
            if (nowMs <= note.StartTimeMs) return false;
            return !note.IsBomb || note.StartTimeMs <= bombCursorMs;
        }
    }
}
