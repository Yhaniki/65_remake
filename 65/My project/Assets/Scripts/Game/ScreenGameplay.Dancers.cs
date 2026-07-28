using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 同場多舞者(M8)。
    ///
    /// 目前這一步只做**共用資產的生成與擺位** —— 也就是「六隻角色同時在場上跳」這件事本身。
    /// 計畫把它排在最前面是刻意的:六隻 CPU 蒙皮的 avatar 是全案最大的效能未知,
    /// 先量得出數字才知道要不要做 LOD/隔幀蒙皮,而不是先寫一套優化再回頭發現不需要。
    /// 量測用 <c>SDO_DANCERS=&lt;n&gt;</c> 打開(見 <see cref="TickDancerPerf"/>)。
    ///
    /// 接遠端真人(誰站哪、名字牌、每個人自己的跳/停)是下一步;這裡先把「多隻在場」的地基打好,
    /// 兩者用的是同一條生成路徑。
    /// </summary>
    public sealed partial class ScreenGameplay
    {
        /// <summary>本機以外的舞者。索引 0 = formation slot 1(slot 0 是本機/領隊)。</summary>
        private readonly List<SdoAvatar> _extraDancers = new List<SdoAvatar>();
        private readonly List<Transform> _extraRoots = new List<Transform>();

        // 共用的解析結果(TryLoadAvatar 存進來)。SdoAvatar 對它們只讀 → 六隻共用安全,
        // 而且省掉五次重讀重解(LoadAsset 沒有快取)。
        private HrcLoader _sharedHrc;
        private MotLoader _sharedDanceMot, _sharedRestMot;
        private DpsLoader _sharedDps;

        /// <summary>
        /// 這一局用哪一種個人隊形(0..2)。由 FrontendApp 從 <c>matchStarting.resolved.formationType</c> 灌進來
        /// (<c>GameSession.Formation==3</c> 的「隨機」由房主抽、server 驗過再 echo,所以每台一定一樣)。
        /// </summary>
        public int formationType;

        /// <summary>這一場總共有幾位舞者(含本機)。量測時由 <c>SDO_DANCERS</c> 覆寫。</summary>
        private int TotalDancers
        {
            get
            {
                var v = DevVar("SDO_DANCERS");
                int n;
                if (!string.IsNullOrEmpty(v) && int.TryParse(v, out n))
                    return Mathf.Clamp(n, 1, FormationCatalog.MaxDancers);
                return Mathf.Clamp(playerCount, 1, FormationCatalog.MaxDancers);
            }
        }

        /// <summary>
        /// 生出本機以外的舞者,擺在官方隊形座標上,並與本機**共用同一份編舞與同一個時鐘**。
        ///
        /// 共用時鐘很重要:同一首歌大家跳一樣(這也是分數流不必傳按鍵記錄的原因)。
        /// 這裡把本機那兩個 delegate 直接指過去,而不是各自算一份時間 —— 各算一份就會慢慢漂開。
        /// </summary>
        private void SpawnExtraDancers()
        {
            // 旁觀者自己沒有舞者(TryLoadAvatar 被跳過),但**別人的還是要出** —— 那正是它要看的東西。
            // 所以這裡不看 spectatorMode,只看有沒有共用資產。
            int total = TotalDancers;
            if (total <= 1 || _sharedHrc == null) return;

            var slots = FormationCatalog.GetSlots(ClampedFormationType, total);
            for (int i = 1; i < total && i < slots.Length; i++)
            {
                var go = new GameObject("Dancer" + i);
                var av = go.AddComponent<SdoAvatar>();
                av.Setup(_sharedHrc, _sharedDanceMot);
                av.SetBodyShape(_bodyShapeB);
                av.RestMot = _sharedRestMot;
                if (_sharedDps != null)
                {
                    av.Dps = _sharedDps;
                    av.MotResolver = ResolveMot;
                    // 🔴 直接沿用本機那兩個 delegate → 同一個時鐘、同一個跳/停判斷。
                    // 各自複製一份算式的話會慢慢漂開(而且沒有測試抓得到「別人的舞者晚半拍」)。
                    av.DanceTimeSec = _avatar != null ? _avatar.DanceTimeSec : null;
                    av.DanceEnabled = _avatar != null ? _avatar.DanceEnabled : null;
                }

                // 用與本機舞者**同一個** builder 與 skin style —— 換成房間那套(SdoRoomAvatar)的話
                // shader/材質不同,場上會出現「其中一隻看起來不一樣」。
                var built = SdoAvatarBuilder.LoadParts(go, av, avatarParts, SdoAvatarBuilder.SkinStyle.Gameplay);
                if (!built.Any) { Destroy(go); continue; }

                float feetY = av.FeetYAt(0f);
                go.transform.position = new Vector3(slots[i].x, slots[i].y - feetY, slots[i].z);
                av.PoseInitialIdle();

                // 遠端舞者刻意**不掛**手光/地面星環/頭上表情:那些是本機專屬的表演元素(官方也只在本機出),
                // 而且每一隻都掛的話成本會直接翻倍。名字牌是之後接真人時才需要的。
                _extraDancers.Add(av);
                _extraRoots.Add(go.transform);
            }
            Debug.Log("[dancers] 生出 " + _extraDancers.Count + " 位額外舞者(總共 " + total + " 位,隊形 "
                      + ClampedFormationType + ")");
        }

        /// <summary>夾進合法範圍的隊形編號(官方只有三張個人隊形座標表)。</summary>
        private int ClampedFormationType => Mathf.Clamp(formationType, 0, FormationCatalog.TypeCount - 1);

        // ================= 效能量測 =================
        // 計畫的 G6:「先量測六個角色同時渲染的效能,再決定要不要優化」。統計本身在 FrameStats
        // (房間那邊也用同一份 —— 房間的最壞情況是 6 座位 + 10 旁觀 = 16 隻,比這裡更重)。

        private FrameStats _perf;

        private void TickDancerPerf()
        {
            if (string.IsNullOrEmpty(DevVar("SDO_DANCERS"))) return;   // 只有量測時才做
            if (_perf == null) _perf = new FrameStats("gameplay");
            _perf.Tick(_extraDancers.Count + (spectatorMode ? 0 : 1));
        }
    }
}
