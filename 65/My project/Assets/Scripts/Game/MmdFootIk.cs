using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 兩節鏈(大腿+小腿)的解析 IK —— 把 MMD 的腳踝拉回「SDO 動作原本把腳踝放的位置」。
    ///
    /// 為什麼需要:aim retarget 只複製**方向**,不複製**長度**。初音的大腿比 SDO 女角短 16.4%、小腿短 2.5%
    /// (整條腿短 9%),所以就算每一節都指對方向,腿一彎腳踝就落到別的地方,而且落差隨姿勢變 —— 那就是腳在
    /// 地上滑、踩不穩的殘餘來源(修掉 センター 甩動和腳掌朝向之後還剩 1.4 倍滑動、腳尖離地約 3% 身高)。
    ///
    /// 做法是標準的餘弦定理解:髖到目標的距離 d 已知、兩節骨長 a/b 已知 → 髖處的夾角 α = acos((a²+d²-b²)/2ad)。
    /// 膝蓋要往哪邊彎不由 IK 決定,而是沿用 aim 已經解出來的膝蓋位置當 hint —— 動作的膝蓋朝向因此被保留,
    /// IK 只負責把末端接回去。
    ///
    /// 構不到的時候(腿比 SDO 短,W_005663 上約 15% 的幀)就伸直朝目標,誤差最多 1% 身高 —— 比不做 IK 的
    /// 3.3% 好,而且不會突然彈一下:伸直是連續的極限狀態。
    /// </summary>
    public static class MmdFootIk
    {
        /// <param name="hip">髖(大腿骨根部)的位置。</param>
        /// <param name="target">腳踝要落到的位置。</param>
        /// <param name="kneeHint">目前(aim 解出來的)膝蓋位置 —— 只用來決定往哪一側彎。</param>
        /// <param name="a">大腿長。</param><param name="b">小腿長。</param>
        /// <param name="thighDir">解出來的大腿方向(已正規化)。</param>
        /// <param name="kneePos">解出來的膝蓋位置。</param>
        /// <returns>false = 這一幀解不了(目標貼在髖上/骨長為 0),呼叫端維持 aim 的結果。</returns>
        public static bool Solve(Vector3 hip, Vector3 target, Vector3 kneeHint, float a, float b,
                                 out Vector3 thighDir, out Vector3 kneePos)
        {
            thighDir = Vector3.down; kneePos = hip;
            if (!(a > 1e-5f) || !(b > 1e-5f)) return false;

            Vector3 toTarget = target - hip;
            float d = toTarget.magnitude;
            if (d < 1e-5f) return false;                       // 目標疊在髖上 —— 沒有定義的解
            Vector3 dirT = toTarget / d;

            if (d >= a + b - 1e-5f)                            // 構不到 → 伸直朝目標(連續,不會彈)
            { thighDir = dirT; kneePos = hip + dirT * a; return true; }
            if (d <= Mathf.Abs(a - b) + 1e-5f) return false;   // 折疊到極限,膝蓋方向沒有意義

            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float alphaDeg = Mathf.Acos(cosA) * Mathf.Rad2Deg;

            // 彎曲平面:由「髖→目標」和「髖→目前膝蓋」張成。兩者共線時(腿正好打直)隨便挑一個垂直軸,
            // 反正那一幀彎曲量趨近 0,挑哪個都看不出來。
            Vector3 axis = Vector3.Cross(dirT, kneeHint - hip);
            if (axis.sqrMagnitude < 1e-10f) axis = Vector3.Cross(dirT, Vector3.forward);
            if (axis.sqrMagnitude < 1e-10f) axis = Vector3.Cross(dirT, Vector3.right);
            if (axis.sqrMagnitude < 1e-10f) return false;
            axis.Normalize();

            thighDir = (Quaternion.AngleAxis(alphaDeg, axis) * dirT).normalized;
            kneePos = hip + thighDir * a;
            return true;
        }
    }
}
