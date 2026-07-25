namespace Sdo.Game
{
    /// <summary>
    /// 長條(hold)身體與尾帽的**貼圖對應**——純幾何、不碰 Unity 物件，方便單元測試（畫圖的部分在
    /// <c>ScreenGameplay.ScrollNotes</c>）。這裡把官方 3D note skin（<c>3DNOTES\LONG.MSH</c> + <c>LONG_0_0/0_1.DDS</c>）
    /// 的規則收成幾個純函式：
    ///
    /// <para><b>錨點永遠在「尾端」。</b>官方 <c>NoteMesh_ClampVertexAlpha_004c28d0</c> 每幀把已經通過判定線的頂點
    /// 夾回判定線，並用 <c>V = 1 − z·(1/31.2)</c> 重算它的 V —— z 是從**尾帽焊點**算起的 mesh 座標。也就是說被「吃掉」
    /// 的是頭那一端，圖樣則死死黏在尾帽上。<see cref="TailY"/> 依捲動方向挑出那一端（向上捲＝下面、向下捲＝上面），
    /// <see cref="DistFromTail"/> 把兩種方向的距離收成同一個非負值。
    /// 這件事錯了會同時壞兩樣：圖樣方向跟尾帽相反，以及「頭被按住釘在判定線 + 長條長到兩端都被裁」時整條凍住不捲動
    /// （兩個 V 都變成常數）。</para>
    ///
    /// <para><b>U 往內縮。</b>官方 mesh 的 U 是 <see cref="OfficialU0"/>..<see cref="OfficialU1"/>，但右緣
    /// (u·128 = 98.34) 剛好切在外側銀色膠囊高光的第一個 texel 上，雙線性取樣會把它混進長條邊緣；膠囊在貼圖裡上下
    /// 是一節一節的，於是邊緣出現忽亮忽暗、跟著捲動跑的「白邊」。<see cref="U0"/>/<see cref="U1"/> 往內縮到純色邊框
    /// 上（左 30.5 / 右 96.0 texel），銀色膠囊這才真的不會被畫到（＝官方 mesh 註解本來的用意）。</para>
    /// </summary>
    public static class HoldBodyUv
    {
        /// <summary>官方 LONG.MSH 身體四邊形的 U 範圍（保留備查；實際取樣用 <see cref="U0"/>/<see cref="U1"/>）。</summary>
        public const float OfficialU0 = 0.2243f, OfficialU1 = 0.7683f;
        /// <summary>實際取樣的 U 範圍：內縮到 LONG_0_1 的純色邊框上（128px 貼圖的 texel 30.5 與 96.0）。
        /// 實測邊緣亮度沿 V 的變化量：左 120→20、右 136→19。</summary>
        public const float U0 = 30.5f / 128f, U1 = 96f / 128f;
        /// <summary>每一個 mesh z 單位的 V 量（官方 1/31.2，見 NoteMesh_ClampVertexAlpha_004c28d0）。</summary>
        public const float VPerUnit = 0.03205128f;
        /// <summary>長條在 mesh 單位下的寬度（≈ 音符寬 21.97）——design px ↔ mesh z 的換算基準。</summary>
        public const float MeshW = 22.0074f;
        /// <summary>尾帽與身體的焊點 z（V ≈ 0.999）。</summary>
        public const float ZBase = 0.0287f;
        /// <summary>尾帽長度 ÷ 長條寬度（尖端 z −10.8154 … 底邊 0.0287）。</summary>
        public const float CapLenRatio = 10.844f / 22.0074f;
        /// <summary>尾帽三角形的 UV：底邊跟身體焊在一起，所以 U 用同一組內縮值，尖端取底邊中點
        /// （官方 0.2255 / 0.7666 / 0.4964）。</summary>
        public const float CapU0 = U0, CapU1 = U1, CapUTip = (U0 + U1) / 2f;
        /// <summary>尾帽三角形取樣 LONG_0_0 的 V：底邊 → 尖端。</summary>
        public const float CapV0 = 0.5574f, CapVTip = 0.8939f;

        /// <summary>尾端（＝離判定線最遠、尾帽焊住的那一頭）的 design y。向上捲(scrollSign +1)＝下面(較大 y)，
        /// 向下捲(−1)＝上面(較小 y)。</summary>
        /// <param name="headY">音符頭目前的 design y（被按住時＝釘在判定線上）。</param>
        /// <param name="endY">音符尾(EndTimeMs)的 design y。</param>
        public static float TailY(float headY, float endY, int scrollSign)
            => scrollSign > 0 ? (headY > endY ? headY : endY) : (headY < endY ? headY : endY);

        /// <summary>某一條邊離尾端多遠（design px，恆 ≥ 0）。乘 scrollSign 把兩種捲動方向收成同一個式子。</summary>
        public static float DistFromTail(float tailY, float edgeY, int scrollSign)
            => scrollSign * (tailY - edgeY);

        /// <summary>官方身體 V：<c>1 − (ZBase + z)·VPerUnit</c>，z = 離尾端的 design px 換算成 mesh 單位。
        /// 尾端本身 → ≈0.999（尾帽那一格），越往頭端 V 越小；超出 0..1 由 wrap(Repeat) 平鋪。</summary>
        /// <param name="distFromTail">見 <see cref="DistFromTail"/>（design px）。</param>
        /// <param name="holdW">長條在畫面上的寬度（design px）＝ mesh 的 <see cref="MeshW"/>。</param>
        public static float BodyV(float distFromTail, float holdW)
        {
            float k = MeshW / (holdW > 1e-3f ? holdW : 1e-3f);
            return 1f - (ZBase + distFromTail * k) * VPerUnit;
        }
    }
}
