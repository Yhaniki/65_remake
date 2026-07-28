using UnityEngine;

namespace Sdo.Game
{
    /// <summary>頭貼(結算列 / 房間)相機的取景 —— **只對頭骨 (Bip01_Head) 的 rest 位置**,不量任何 mesh。
    ///
    /// 骨架每套裝扮都是同一副 → 相機距離與瞄準點是相對頭骨的固定值,換髮型、戴帽子、穿翅膀,頭都恆等大、恆等位置。
    /// (使用者:「不該算臉或頭髮,就是對頭的骨骼的位置就好」。)
    ///
    /// 之前是量 renderer bounds 的髮頂再算 dist=1.9×h:穿「Ribbon Star M」(iteminfo 37939 / cat 8 翅膀 =
    /// 037939_MAN_CHIBANG.MSH)時翅膀頂比髮頂高一大截 —— 實測髮頂 67.73、頭骨 55.12(h=12.61),翅膀頂 86.62
    /// (h=31.50,**2.5 倍**)→ 相機退遠 2.5 倍、瞄準點跟著上飄 → 結算大頭貼變成框裡的小人。量任何幾何都會踩到
    /// 這類離群配件,所以改成完全不量。
    ///
    /// 下面的常數就是照「翅膀不算進去」的那個正確構圖反推(模型單位,與 avatar scale 無關):
    /// 頭骨→髮頂 12.62 → dist = 1.9×12.62 = 23.97、瞄準點抬 0.35×12.62 = 4.42。
    /// </summary>
    public static class HeadBoneFraming
    {
        /// <summary>相機到瞄準點的距離(模型單位;世界值 = ×avatar scale)。頭部近拍:頭填滿框,頭髮往上溢出。</summary>
        public const float DistModel = 23.97f;
        /// <summary>瞄準點從頭骨往上抬多少(模型單位)→ 臉落在框內偏下、髮頂留在框上緣外。</summary>
        public const float AimUpModel = 4.42f;

        /// <summary>算出相機的瞄準點與距離。<paramref name="aimOffsetModel"/> = 相對頭骨的瞄準偏移(模型單位,
        /// 通常 X 用來把臉擺正、Y 用 <see cref="AimUpModel"/>);<paramref name="zoom"/> &gt;1 = 拉遠。</summary>
        public static void Compute(Vector3 headBoneWorld, float scale, float zoom, Vector3 aimOffsetModel,
                                   out Vector3 target, out float dist)
        {
            float s = Mathf.Max(0.01f, scale);
            target = headBoneWorld + aimOffsetModel * s;
            dist = DistModel * s * Mathf.Max(0.05f, zoom);
        }
    }
}
