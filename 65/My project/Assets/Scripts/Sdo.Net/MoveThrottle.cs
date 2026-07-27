namespace Sdo.Net
{
    /// <summary>
    /// 決定「這一幀要不要把自己的位置送出去」。
    ///
    /// 純邏輯、零 Unity 依賴 —— 因為這是整個走路同步裡**最容易寫錯而且最難用眼睛驗**的一塊:
    /// 少送一筆「停下」對方就會看到一個原地跑步的人;多送就是每幀灌 60 個封包。
    /// 抽出來之後可以直接單元測試。
    ///
    /// 規則(四條,順序有意義):
    ///   ① 站著不動 → **永不送**(站著的人不需要每 100ms 提醒別人他還在那裡)
    ///   ② 開始走 / 停下 / 轉向 → 立刻送(edge —— 這些是別人看得出來的瞬間)
    ///   ③ 走動中 → 每 <see cref="NetLimits.ClientMoveIntervalMs"/> 送一次
    ///   ④ 「停下」那一筆送完就靜音,直到下次又開始走
    /// </summary>
    public sealed class MoveThrottle
    {
        private bool _hasSent;
        private long _lastSentMs;
        private float _lastX, _lastZ, _lastFacing;
        private bool _lastWalking;

        /// <summary>朝向變化超過這個角度才算「轉向」。房間裡的朝向只有四個方向(0/90/180/270),所以門檻可以很寬。</summary>
        private const float FacingEpsilon = 1f;

        /// <summary>
        /// 「瞬移」的門檻(unit)。只有真的被搬動(座位補正、未來的傳送)才該無視節奏立刻回報。
        ///
        /// 🔴 這個數字踩過一次很嚴重的坑:原本寫成 2 unit,但房間走路速度是 **60 unit/秒**
        /// (RoomMovement:dtMs × 0.02 × 3),60fps 下每幀移動 1 unit → 每兩幀就觸發一次 = 30 送/秒,
        /// 超過 server 的 15/秒上限 → strikes 累積 → **走不到兩秒就被踢下線**。
        /// 走路本來就有 100ms 的定期回報(6 unit/次)在管,所以這個門檻必須遠大於那個距離。
        /// 30 unit = 半秒的走路距離,走路永遠碰不到,真的被搬動一定超過。
        /// </summary>
        private const float TeleportDist = 30f;

        /// <summary>離開房間 / 重連 → 忘掉上次送的東西,下一次一定會重送。</summary>
        public void Reset()
        {
            _hasSent = false;
            _lastSentMs = 0;
            _lastWalking = false;
        }

        /// <summary>要送嗎?回 true 的話呼叫端就送,並且這裡已經把「上次送的」記下來了。</summary>
        public bool ShouldSend(float x, float z, float facing, bool walking, long nowMs)
        {
            bool send;
            if (!_hasSent)
            {
                // 第一筆一定送:那是「我在這裡」的宣告,不送的話別人只能把我放在 fallback 點。
                send = true;
            }
            else if (walking != _lastWalking)
            {
                send = true;                                  // ② 開始走 / 停下
            }
            else if (Dist2(x, z, _lastX, _lastZ) > TeleportDist * TeleportDist)
            {
                // 真的被搬動了(座位補正、未來的傳送)。
                // 🔴 這條必須排在「站著不動就不送」**前面** —— 座位補正正好是站著被搬動的,
                // 排在後面的話那一筆永遠送不出去,別人看到的位置就停在舊的地方
                // (而症狀是「兩隻角色疊在一起」,看起來像位置同步整個沒生效)。
                send = true;
            }
            else if (!walking)
            {
                send = false;                                 // ① + ④ 站著不動就不送
            }
            else if (Abs(facing - _lastFacing) > FacingEpsilon)
            {
                send = true;                                  // ② 轉向
            }
            else if (nowMs - _lastSentMs >= NetLimits.ClientMoveIntervalMs)
            {
                // ③ 走動中的定期回報。位置完全沒動(撞牆)也照送 —— 對方要靠它知道我還在走
                //    (不送的話 RemoteWalkTimeout 會把他畫成停下,而我明明還在推著牆走)。
                send = true;
            }
            else send = false;

            if (!send) return false;
            _hasSent = true;
            _lastSentMs = nowMs;
            _lastX = x; _lastZ = z; _lastFacing = facing; _lastWalking = walking;
            return true;
        }

        private static float Abs(float v) => v < 0f ? -v : v;

        private static float Dist2(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx, dz = az - bz;
            return dx * dx + dz * dz;
        }
    }
}
