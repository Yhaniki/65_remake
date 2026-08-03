namespace Sdo.Net
{
    /// <summary>
    /// 「這位遠端玩家還在這一場裡嗎」——純規則,給打歌畫面判斷別人的角色該不該繼續跳。
    ///
    /// 為什麼需要它:遠端舞者的跳/停是從分數流的差推出來的(<c>Sdo.Ruleset.DanceGate</c>),
    /// 而**離場的人不會再送 frame** → 每個結算點都是空 block → 規則(3)維持現況 → 他離場那一刻
    /// 要是正在跳,場上那尊就會一路跳到曲末。分數流本身推不出「他人已經不在了」,只有座位的
    /// playState 知道。
    /// </summary>
    public static class MatchPresence
    {
        /// <summary>這個 playState 算不算「還在這一場裡」(打完歌在看結算也算 —— 人還沒回房間)。</summary>
        public static bool InMatch(PlayState state)
            => state == PlayState.Playing || state == PlayState.Finished || state == PlayState.Results;

        /// <summary>
        /// 他離場了嗎。<paramref name="state"/> = null 代表**座位不在了**(離開房間 / 斷線)。
        ///
        /// 🔴 <paramref name="sawPlaying"/> 這道 latch 不能省:開跳前座位還停在 loaded/readyForGameplay,
        /// 那時直接看「不是 playing 就是離場」會讓全場遠端一開跳就站著不動。
        /// 要**先看過他真的在 playing**,之後掉出這一場才算離場。
        /// </summary>
        public static bool HasLeft(PlayState? state, bool sawPlaying)
        {
            if (!state.HasValue) return true;          // 座位沒了 = 人走了(Esc 回房後又離開房間 / 斷線)
            return sawPlaying && !InMatch(state.Value);
        }
    }
}
