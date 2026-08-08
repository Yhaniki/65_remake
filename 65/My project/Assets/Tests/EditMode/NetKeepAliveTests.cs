using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 心跳的存活契約 —— 守的是使用者回報的:「收到遠端的 mmd 檔案會整個凍結一下,可能會造成 server 以為斷線」。
    ///
    /// 真正的機制:server 的 <c>SweepDeadConnections</c> 判死看的是「多久沒**收到**這條連線的東西」
    /// (<see cref="NetLimits.PingTimeoutMs"/>)。舊版的 ping 全都是主執行緒排隊的
    /// (<c>NetClient.Tick</c> / <c>NetSongFetcher.KeepAlive</c>),所以主執行緒一卡住(解別人的 .pmx、
    /// 解碼十張 2048² 貼圖、換場景、載歌)ping 就跟著停 —— 「本機在忙」在 server 眼中與「這台機器死了」
    /// 完全一樣。現在改由 <c>NetConnection</c> 的 writer thread 自己補,判斷就是這裡這一條。
    /// </summary>
    public class NetKeepAliveTests
    {
        [Test]
        public void NotDue_BeforeTheInterval()
        {
            Assert.IsFalse(NetLimits.KeepAliveDue(1000, 1000), "剛送完就又補一個 —— 每 500 ms 一個 ping 是白費頻寬");
            Assert.IsFalse(NetLimits.KeepAliveDue(1000 + NetLimits.PingIntervalMs - 1, 1000));
        }

        [Test]
        public void Due_AtAndAfterTheInterval()
        {
            Assert.IsTrue(NetLimits.KeepAliveDue(1000 + NetLimits.PingIntervalMs, 1000));
            Assert.IsTrue(NetLimits.KeepAliveDue(1000 + NetLimits.PingIntervalMs * 10, 1000));
        }

        /// <summary>
        /// 主執行緒凍住整整 <see cref="NetLimits.PingTimeoutMs"/> 也不能被判死 —— 那正是這個修正的重點。
        /// 這裡直接模擬 writer thread 的迴圈:每 <see cref="NetLimits.KeepAlivePollMs"/> 取樣一次,
        /// 期間**沒有任何**主執行緒排進來的訊息(就是「凍住」的定義),看送出去的間隔會不會超過判死線。
        /// </summary>
        [Test]
        public void AFrozenMainThread_StillGetsAPingWellInsideTheServersDeadline()
        {
            long lastSent = 0;
            long worstGap = 0;
            for (long now = 0; now <= 60_000; now += NetLimits.KeepAlivePollMs)
            {
                if (!NetLimits.KeepAliveDue(now, lastSent)) continue;
                worstGap = System.Math.Max(worstGap, now - lastSent);
                lastSent = now;
            }
            Assert.Less(worstGap, NetLimits.PingTimeoutMs,
                        "主執行緒凍住一分鐘的話,補出來的心跳間隔已經超過 server 的判死線 —— 玩家會被踢掉");
            Assert.LessOrEqual(worstGap, NetLimits.PingIntervalMs + NetLimits.KeepAlivePollMs,
                        "實際間隔被取樣量化得比預期粗");
        }

        /// <summary>
        /// 取樣間隔要比心跳間隔細很多,否則實際的心跳間隔會被量化成取樣的倍數(取樣 ＝ 心跳時最壞會變成兩倍)。
        /// 而心跳間隔本身要在判死線內留足夠餘裕,擋得住「補的那一個剛好掉包」。
        /// </summary>
        [Test]
        public void TheThreeConstants_LeaveRoomForALostPing()
        {
            Assert.Less(NetLimits.KeepAlivePollMs, NetLimits.PingIntervalMs / 2,
                        "取樣間隔太粗 —— 心跳的實際間隔會被量化放大");
            Assert.GreaterOrEqual(NetLimits.PingTimeoutMs, NetLimits.PingIntervalMs * 3,
                        "判死線不到心跳間隔的三倍 —— 掉一兩個 ping 就被踢");
        }
    }
}
