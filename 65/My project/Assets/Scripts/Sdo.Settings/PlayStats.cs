using System;

namespace Sdo.Settings
{
    /// <summary>
    /// 一個角色的**累計**遊玩統計 —— 存在該角色的 <c>profile.json</c> 裡,個人資料頁的各種百分比全由它算出來。
    ///
    /// 為什麼是累計而不是每場一筆:個人頁要的是「總命中率 / 總勝率」,存每場明細只是為了同一個總和多出
    /// 一份會無限長大的資料。真的要看單場成績,結算畫面當下就有。
    ///
    /// 判定數用 <see cref="long"/>:一首歌上千個音符,玩幾千首就會逼近 int 的邊界。
    /// </summary>
    [Serializable]
    public class PlayStats
    {
        // ---- 累計判定數(每首歌結束時把該場的 ScoreProcessor 計數加上來)----
        public long perfect;
        public long cool;
        public long bad;
        public long miss;

        /// <summary>完成的歌數(中途離開不算 —— 那不是一場完整的成績)。</summary>
        public int plays;

        // ---- 勝負。**只有連線對局的普通模式與 ShowTime 模式**才會動(見 RecordsWinLoss)。----
        //      自由模式沒有名次可言、旁觀不是參賽者、單機的對手是假資料 —— 那三種記了只會讓勝率失去意義。
        public int wins;
        public int losses;

        /// <summary>總判定數 = P+C+B+M。命中率的分母。</summary>
        public long Judged { get { return perfect + cool + bad + miss; } }

        /// <summary>命中率 %:<c>(Perfect + Cool) / 總判定</c> —— 與結算畫面、Grade 用的是同一條式子。</summary>
        public double Accuracy { get { return Percent(perfect + cool); } }

        public double PerfectRate { get { return Percent(perfect); } }
        public double CoolRate { get { return Percent(cool); } }
        public double BadRate { get { return Percent(bad); } }
        public double MissRate { get { return Percent(miss); } }

        /// <summary>勝率 %。一場都還沒打(分母 0)回 0 —— 不是 100 也不是 NaN。</summary>
        public double WinRate
        {
            get
            {
                int total = wins + losses;
                return total > 0 ? wins * 100.0 / total : 0.0;
            }
        }

        private double Percent(long n)
        {
            long judged = Judged;
            return judged > 0 ? n * 100.0 / judged : 0.0;
        }

        /// <summary>把一場的判定數加進累計。負數一律當 0(壞掉的來源不該把累計拉低)。</summary>
        public void AddPlay(int p, int c, int b, int m)
        {
            perfect += Math.Max(0, p);
            cool += Math.Max(0, c);
            bad += Math.Max(0, b);
            miss += Math.Max(0, m);
            plays++;
        }

        public void AddResult(bool won)
        {
            if (won) wins++; else losses++;
        }

        /// <summary>
        /// 這個模式要不要記勝負?<c>1 = 普通模式、2 = ShowTime 模式</c> 才記(見 <c>GameSession.GameMode</c>)。
        ///
        /// 抽成具名函式是因為模式代號在專案裡是裸 int,散在 GameSession / RoomConfig / NetRoomSettings 三處;
        /// 「哪些模式算戰績」這條政策只該有一份。
        /// </summary>
        public static bool RecordsWinLoss(int gameMode)
        {
            return gameMode == 1 || gameMode == 2;
        }

        public void Sanitize()
        {
            if (perfect < 0) perfect = 0;
            if (cool < 0) cool = 0;
            if (bad < 0) bad = 0;
            if (miss < 0) miss = 0;
            if (plays < 0) plays = 0;
            if (wins < 0) wins = 0;
            if (losses < 0) losses = 0;
        }
    }

    /// <summary>
    /// 本機好友清單的一筆。**存在自己的 profile.json 裡** —— server 目前沒有任何帳號持久化
    /// (廣播完房間結果就把資料丟掉),所以好友是這台機器記得的東西,不是伺服器記得的。
    ///
    /// <see cref="id"/> 存的是對方的 <c>playerId</c>(對方本機存檔的編號,握手時會報上來),
    /// 而不是 server 每次連線重發的 userId —— 後者下次上線就變了,拿來當好友的鍵會整串失效。
    /// </summary>
    [Serializable]
    public class FriendEntry
    {
        public string id = "";
        public string name = "";
        public string addedAt = "";
    }
}
