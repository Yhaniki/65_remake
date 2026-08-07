namespace Sdo.Game
{
    /// <summary>一隻角色身上該畫哪一具身體。</summary>
    public enum MmdSource
    {
        /// <summary>SDO 原本的穿搭（沒有 MMD，或設定不要）。</summary>
        Sdo,
        /// <summary>本機玩家自己選的那個模型。</summary>
        LocalModel,
        /// <summary>那個人自己宣告的模型（packId）。</summary>
        RemoteModel,
    }

    /// <summary>
    /// 「這一隻要顯示哪一具身體」的決策 —— 抽出來當純函式，因為它踩過一個很難從畫面上看出根因的 bug：
    /// <b>本機選的模型被套到了別人身上</b>。當時遠端角色與本機角色共用同一個「沒有 packId ⇒ 用設定裡選的那個」
    /// 回退路徑，於是同房的人沒穿 MMD（packId 空）時，就被畫成了我自己的模型。
    ///
    /// 現在這裡把兩件事分得死死的，而且它們是**兩個獨立的功能**：
    ///   • <b>我要不要用 MMD 模型</b> —— 看本機有沒有選模型（<c>RoomConfig.mmdModel</c>，「(不使用)」＝不用）。
    ///     沒有第二個總開關：選了就是要用。
    ///   • <b>我要不要看到別人的 MMD 模型</b> —— <c>RoomConfig.mmdShowOthers</c>。
    /// 兩者互不影響：可以自己維持 SDO 角色卻看得到別人的 MMD，也可以反過來。
    ///
    /// 遠端角色<b>永遠只可能</b>畫他自己宣告的那個模型；他沒宣告就是他的 SDO 穿搭，絕不回退到本機選的模型。
    /// </summary>
    public static class MmdDisplayPolicy
    {
        /// <param name="remote">這一隻是別人（不是本機玩家）嗎。</param>
        /// <param name="declaredPack">遠端角色的外觀宣告的模型 packId（空＝他沒穿 MMD）。</param>
        /// <param name="localModelSelected">本機設定裡選了一個裝得到的模型嗎。</param>
        /// <param name="showOthers">要顯示別人的 MMD 模型嗎（<c>mmdShowOthers</c>）。</param>
        public static MmdSource SourceFor(bool remote, string declaredPack, bool localModelSelected, bool showOthers)
        {
            if (remote)
                return showOthers && !string.IsNullOrEmpty(declaredPack) ? MmdSource.RemoteModel : MmdSource.Sdo;
            return localModelSelected ? MmdSource.LocalModel : MmdSource.Sdo;
        }
    }

    /// <summary>一具 MMD 身體「多大、布料什麼手感」的那四個數值。</summary>
    public struct MmdRigTuning
    {
        /// <summary>自動對齊舞者身高之後再乘的倍率(config.ini <c>mmdScale</c>)。</summary>
        public float SizeMul;
        /// <summary>布料重力倍率(config.ini <c>mmdGravity</c>)。</summary>
        public float Gravity;
        /// <summary>布料剛性,面板刻度(config.ini <c>mmdStiffness</c>)。</summary>
        public float Stiffness;
        /// <summary>碰撞體半徑倍率(config.ini <c>mmdColliderScale</c>)。</summary>
        public float ColliderScale;
    }

    /// <summary>
    /// 這一具身體該吃誰的數值 —— 與 <see cref="MmdDisplayPolicy"/> 同一條界線的另一半:
    /// <b>本機那幾根旋鈕是「我調我自己的模型」用的,不是拿去調別人的模型。</b>
    ///
    /// 別人的模型自帶它自己的參數:模型資料夾裡的 <c>physics.ini</c>(<see cref="MmdClothProfile"/>),
    /// 沒有那個檔就是從他的 .pmx 剛體/關節轉出來的值。那份檔案是**跟著模型包一起傳過來的**
    /// (見 <c>ModelPackFilter</c> 的 companion:跟著走,但不進 packId),用意就是「別人看到的
    /// 就是你調好的樣子」。建完卻馬上用本機 config.ini 的旋鈕再蓋一次,等於把那份檔案作廢 ——
    /// 症狀:剛下載好別人的模型,房間上面那格頭貼裡他的頭髮被撐得膨起來(碰撞半徑差 1.5 倍),
    /// 而同一具模型在他自己畫面上是正常的。所以布料那三根對遠端一律是中性值。
    ///
    /// <b>大小(<see cref="MmdRigTuning.SizeMul"/>)是唯一會跟著人走的那一個</b> —— 但走的是
    /// **他宣告的值**(<c>NetAvatarLook.MmdScale</c>,隨外觀廣播),不是我這台的旋鈕:
    ///   • 拿我的旋鈕去乘別人的模型 = 把他變形,而且 <c>mmdScale</c> 改動時只重建本機那幾隻、
    ///     遠端卻會在**下一次新建**時吃到 → 先載的人正常、後載的人變形,同一個房間兩種大小。
    ///   • 完全不傳(舊版的做法)= 他在自己畫面上與在我畫面上是兩個大小,而且頭上的名字牌高度是
    ///     照畫出來的身高算的(<see cref="MmdHeadroom"/>),於是我這邊還會看到名字插進他頭裡。
    /// 他沒宣告(舊 client / 他沒調過)就是 <see cref="NeutralSize"/> ＝ 只做自動對齊身高。
    /// </summary>
    public static class MmdTuningPolicy
    {
        public const float NeutralSize = 1f;
        public const float NeutralGravity = 1f;
        /// <summary>面板的預設剛性 —— <c>MmdAvatar.TunePhysics</c> 拿它當基準(stiffMul 剛好 1×)。</summary>
        public const float NeutralStiffness = 0.12f;
        public const float NeutralCollider = 1f;

        /// <summary>什麼都不調的那一組(＝完全照模型自己的參數)。</summary>
        public static MmdRigTuning Neutral => new MmdRigTuning
        {
            SizeMul = NeutralSize, Gravity = NeutralGravity,
            Stiffness = NeutralStiffness, ColliderScale = NeutralCollider,
        };

        /// <param name="remote">這一隻是別人(不是本機玩家)嗎。</param>
        /// <param name="sizeMul">config.ini <c>mmdScale</c>。</param>
        /// <param name="gravity">config.ini <c>mmdGravity</c>。</param>
        /// <param name="stiffness">config.ini <c>mmdStiffness</c>。</param>
        /// <param name="colliderScale">config.ini <c>mmdColliderScale</c>。</param>
        /// <param name="declaredSize">**遠端專用**:他自己宣告的大小倍率(<c>NetAvatarLook.MmdScale</c>)。
        /// 本機那一隻不看這個值。</param>
        public static MmdRigTuning For(bool remote, float sizeMul, float gravity, float stiffness, float colliderScale,
                                       float declaredSize = NeutralSize)
            => remote
             ? new MmdRigTuning
               {
                   SizeMul = Sdo.Osu.MmdModelRef.ClampScale(declaredSize),   // 他調的大小要跟著他
                   Gravity = NeutralGravity, Stiffness = NeutralStiffness, ColliderScale = NeutralCollider,
               }
             : new MmdRigTuning { SizeMul = sizeMul, Gravity = gravity, Stiffness = stiffness, ColliderScale = colliderScale };
    }
}
