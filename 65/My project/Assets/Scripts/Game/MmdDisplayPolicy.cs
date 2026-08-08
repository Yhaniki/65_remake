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

    /// <summary>一份**別人的**模型現在該怎麼上身。</summary>
    public enum MmdBuildPlan
    {
        /// <summary>這一輪什麼都別做。</summary>
        Skip,
        /// <summary>直接建 —— 只剩骨架＋布料,實測 11~26 ms。</summary>
        BuildNow,
        /// <summary>先分幀預熱(解析 .pmx、一幀一張貼圖、共用材質/mesh),之後才建。</summary>
        Stage,
    }

    /// <summary>
    /// 別人的模型「這一輪直接建,還是先分幀預熱」。
    ///
    /// 🔴 <b>一份冷的模型絕對不能在一幀裡建起來。</b>量過(初音):.pmx 解析 89 ms ＋ 共用資產 1438 ms
    /// (其中十張 2048² PNG 的解碼佔 1401 ms)。本機自己那一個靠開機的 <c>PrewarmCo</c> 把這 1.5 秒藏在
    /// 載入畫面後面,但**別人的模型是遊戲中途才到的** —— 那時沒有載入畫面,畫面就是整個凍住。
    ///
    /// 而且不只是難看:ping 是主執行緒排隊送的,主執行緒凍住 ping 就停,而 server 判死看的是
    /// 「多久沒收到這條連線的東西」(<c>NetLimits.PingTimeoutMs</c> 15 秒)——
    /// 一份大模型、一顆慢磁碟、或同時到了好幾份,就足以讓一個還活著的玩家被踢掉。
    /// (那一半由 <c>NetConnection</c> 的 writer thread 自己補心跳擋掉;這裡是另一半:別讓畫面凍。)
    ///
    /// 反過來說,<b>暖的就要當場建</b>:那只剩骨架＋布料(11~26 ms),走預熱只會讓他晚三幀才出現,
    /// 而同房第二個人穿同一份模型時走的正是這條路。
    /// </summary>
    public static class MmdStagingPolicy
    {
        /// <param name="modelReady">本機已經有這份模型、而且找得到要載的 .pmx 了嗎
        /// (還在下載 / 他沒穿 / 我不看別人的 → false)。</param>
        /// <param name="alreadyStaging">這份模型已經有一趟預熱在跑了嗎(同一包可能有好幾隻在等)。</param>
        /// <param name="sharedAssetsWarm">這份模型的共用 mesh/材質/貼圖已經在快取裡了嗎
        /// (<c>MmdAvatar.IsWarm</c>;**還沒解析過 .pmx 就算冷的** —— 解析本身就要幾十到幾百 ms)。</param>
        /// <param name="onStage">本機現在在舞台上打歌嗎。<b>打歌中一律不開始新的預熱</b> ——
        /// 即使已經分幀,一張 2048² 的 <c>Apply</c> 仍有 20~30 ms,連續十幾幀正是節奏遊戲最不能有的東西。
        /// 模型晚幾分鐘上身沒有代價:歌一結束,補建迴圈(0.25 秒一輪)就會把它接上去。
        /// 反過來,**已經是暖的就照建** —— 那只剩 11~26 ms 的骨架,而且遠端舞者一上台就需要它,
        /// 拖到歌尾只會讓他整首歌都是 SDO 穿搭。</param>
        public static MmdBuildPlan For(bool modelReady, bool alreadyStaging, bool sharedAssetsWarm, bool onStage = false)
        {
            if (alreadyStaging) return MmdBuildPlan.Skip;   // 已經在預熱了 —— 再排一趟只是重複付錢
            if (!modelReady) return MmdBuildPlan.Skip;      // 還沒東西可建(這不是失敗,他就先維持 SDO 穿搭)
            if (sharedAssetsWarm) return MmdBuildPlan.BuildNow;
            return onStage ? MmdBuildPlan.Skip : MmdBuildPlan.Stage;
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
