using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 房間裡「其他舞者站哪」的取樣器(座位 1..5)。
    ///
    /// 官方離線的 EXE 把六個舞者**全部生在同一點**(<c>RoomLayout.HostSpawn</c>),再靠 server 的
    /// move 封包把人挪開 —— 也就是說官方的離線 client 根本沒有站位表。我們的連線層目前也不在
    /// 進房那一刻送位置(位置流只在有人走動時才送),所以需要一組「每台自己算、但算出來一樣」的站位:
    /// 沒有它的話六個人會疊在房主身上,六張名字牌重疊成一團。
    ///
    /// 🔴 **跨機器一致是硬需求**,所以這裡:
    ///   • 用 <see cref="Seed"/> 固定種子的 <see cref="System.Random"/> —— 不碰 <c>UnityEngine.Random</c>
    ///     (那是全域狀態:別的系統抽一次就會讓每台的序列漂開),也不碰時間。
    ///   • 可走判定由呼叫端注入(房間是 MASK.MSK,那是 DATA 裡同一份檔)。
    ///   • 純函式、零 MonoBehaviour → 直接單元測試,而「兩台看到的站位一不一樣」這種 bug
    ///     在實機上很難發現(要兩台並排比對),測試裡卻是一行 assert。
    ///
    /// 取樣方式:圓盤內均勻(半徑取 <c>sqrt(u)</c> —— 直接用 u 的話點會擠在圓心),
    /// 再用拒絕採樣濾掉三種點:不可走的、離 <paramref name="avoid"/>(房主)太近的、離已選點太近的。
    /// 所以**結構上就不會有兩個人站在一起**。
    /// </summary>
    public static class DancerSpotSampler
    {
        /// <summary>固定種子。換這個值 = 換一組站位(所有 client 都要一起換,不然會不一致)。</summary>
        public const int Seed = 0x5D0;

        /// <summary>取樣次數上限。可走區域太小/間距要求太嚴時,拿得到幾個就回幾個(不無限迴圈)。</summary>
        public const int MaxTries = 9000;

        /// <param name="count">要幾個點。</param>
        /// <param name="center">取樣圓盤的中心(XZ)。</param>
        /// <param name="radius">取樣圓盤半徑。</param>
        /// <param name="spacing">彼此、以及與 <paramref name="avoid"/> 的最小距離。</param>
        /// <param name="avoid">要避開的既有位置(房主的出生點)。</param>
        /// <param name="floorY">回傳點的 Y。</param>
        /// <param name="walkable">(x,z) → 這裡站得住嗎。null = 全部可走。</param>
        /// <returns>最多 <paramref name="count"/> 個點;湊不滿就回比較少(絕不回 null)。</returns>
        public static Vector3[] Sample(int count, Vector2 center, float radius, float spacing,
                                       Vector2 avoid, float floorY, Func<float, float, bool> walkable)
        {
            var pts = new List<Vector3>();
            if (count <= 0) return pts.ToArray();

            var rng = new System.Random(Seed);
            Fill(pts, count, rng, center, radius, spacing, avoid, floorY, walkable, enforceSpacing: true);

            // 🔴 湊不滿就**放寬間距再補**(只要求站得住)。
            // 為什麼一定要湊滿:呼叫端是用 `index % Length` 去取點的,長度不足時兩個座位會繞回
            // **同一個 index** —— 那是確定會發生的完全重疊(兩隻角色疊在一起),比「站得近一點」糟得多。
            // 這一輪沿用同一個 rng(接著往下抽,不是重新開始)—— 重新開始會抽到與第一輪一樣的候選點,
            // 而少了間距檢查它們就會被原封收下,變成真正的重複座標。
            if (pts.Count < count)
                Fill(pts, count, rng, center, radius, spacing, avoid, floorY, walkable, enforceSpacing: false);

            return pts.ToArray();
        }

        private static void Fill(List<Vector3> pts, int count, System.Random rng,
                                 Vector2 center, float radius, float spacing, Vector2 avoid, float floorY,
                                 Func<float, float, bool> walkable, bool enforceSpacing)
        {
            float sp2 = spacing * spacing;
            for (int guard = 0; guard < MaxTries && pts.Count < count; guard++)
            {
                // 🔴 兩個 NextDouble 的順序固定 —— 順序換了就是另一組站位。
                double ang = rng.NextDouble() * 6.2831853;
                double rad = Math.Sqrt(rng.NextDouble()) * radius;
                float x = center.x + (float)(rad * Math.Cos(ang));
                float z = center.y + (float)(rad * Math.Sin(ang));
                if (walkable != null && !walkable(x, z)) continue;

                if (enforceSpacing)
                {
                    var v = new Vector2(x, z);
                    if ((v - avoid).sqrMagnitude < sp2) continue;
                    bool clash = false;
                    for (int i = 0; i < pts.Count; i++)
                        if ((new Vector2(pts[i].x, pts[i].z) - v).sqrMagnitude < sp2) { clash = true; break; }
                    if (clash) continue;
                }
                pts.Add(new Vector3(x, floorY, z));
            }
        }
    }
}
