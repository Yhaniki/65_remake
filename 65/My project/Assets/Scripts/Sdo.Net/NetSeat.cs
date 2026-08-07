using System.Collections.Generic;

namespace Sdo.Net
{
    /// <summary>
    /// 玩家的身分資料(頭上名牌那一組:名字 / 家族 / 等級,加上本機的 playerId)。
    ///
    /// 為什麼它是一份**會變的**資料、而不是握手時報一次就算數:**選性別 == 選帳號** ——
    /// 女角與男角是兩個 profile,各有自己的名字,而握手發生在開機時(那時還沒選)。
    /// 見 <see cref="NetProto.SetIdentity"/>。
    ///
    /// 與 <see cref="NetAvatarLook"/> 分開送:外觀變動會讓收端**重建角色**(讀十幾個部件檔),
    /// 改名字不該付那個代價;而且 server 端對這兩者的權限規則不同(名字可能被 token 綁死)。
    /// </summary>
    public sealed class NetPlayerIdentity
    {
        /// <summary>顯示名稱。</summary>
        public string Name = "";

        /// <summary>本機的 profile id(DATA/PROFILE 的資料夾名)。server 只是存著,座位一律用 userId 當 key。</summary>
        public string PlayerId = "";

        /// <summary>家族名。空 = 沒有家族(頭上名牌不顯示那一行)。</summary>
        public string Guild = "";

        /// <summary>
        /// 家族徽章(DATA/EMBLEM 底下的名字,如 <c>SMALL43</c>)。空 = 只顯示家族名、不放徽章。
        ///
        /// 為什麼要上網:別人頭上的家族列要畫出徽章(以前只有本機看得到自己的),
        /// 而且**同族的判定是名字+徽章兩者**(見 <see cref="GuildIdentity"/>)。
        /// </summary>
        public string GuildEmblem = "";

        /// <summary>等級(頭上名牌的 Lv)。</summary>
        public int Level;

        /// <summary>
        /// 兩份身分一樣嗎?client 用它做「送出去重」—— 每送一次 server 就 rev++ 並向全房
        /// 廣播一份完整快照,而多數時候身分根本沒變(進房、打完歌回房都會呼叫)。
        /// null 與空字串視為相等。
        /// </summary>
        public bool SameAs(NetPlayerIdentity o)
        {
            if (o == null) return false;
            return Level == o.Level
                && Same(Name, o.Name) && Same(PlayerId, o.PlayerId)
                && Same(Guild, o.Guild) && Same(GuildEmblem, o.GuildEmblem);
        }

        private static bool Same(string a, string b)
            => string.Equals(a ?? "", b ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 玩家的外觀資料 —— 房間 3D 與遊戲中的舞者都要靠它把別人的角色建出來。
    ///
    /// <see cref="Parts"/> 對映 client 端 <c>AvatarOutfit.ResolveParts</c> 的輸出
    /// (<c>SdoRoomAvatar.Build</c> 的 <c>equippedParts</c> 參數,型別是 <c>string[]</c>)。
    /// </summary>
    public sealed class NetAvatarLook
    {
        /// <summary>穿戴部件數上限。正常一套裝備約 10 件,32 留了很寬的餘裕。</summary>
        public const int MaxParts = 32;

        /// <summary>單一部件名稱的長度上限。</summary>
        public const int MaxPartNameLength = 64;

        /// <summary>0=女 1=男。決定用哪套骨架(FEMALE.HRC / MALE.HRC)與哪些動作。</summary>
        public int Gender;

        /// <summary>體型 index 0..4(0=瘦)。見 <c>SdoBodyShape.WeightFromIndex</c>。</summary>
        public int BodyIndex;

        /// <summary>穿戴的部件。null 或空 = 用預設外觀。</summary>
        public string[] Parts;

        /// <summary>
        /// 這個人身上顯示的 MMD 模型,以 <c>ModelPackId</c>(<c>sha256:</c>+32 hex)表示;空 = 不用 MMD、
        /// 就是上面那套 SDO 穿搭。
        ///
        /// **為什麼放在外觀裡而不是另開一條訊息**:它就是外觀的一部分 —— 房間快照本來就每次帶著整份
        /// <c>look</c> 廣播,而「要不要重建這隻角色」的判斷(<see cref="Key"/>)也已經在這裡。另開一條路
        /// 的話那兩件事都要再做一次,而且會出現「穿搭到了但模型還沒到」的中間狀態。
        ///
        /// 收到這個 id 但本機沒有那份模型時,**照樣先用 <see cref="Parts"/> 把 SDO 身體建出來** ——
        /// MMD 模型本來就是疊在 SDO 骨架上顯示的(SDO 那隻是動作驅動器),所以「還沒下載完」的正確畫面
        /// 天生就是他的 SDO 穿搭,下載完再把身體換掉,不必重建、不會有空白。
        /// </summary>
        public string MmdPack = "";

        /// <summary>
        /// <see cref="MmdPack"/> 那一包裡,他實際穿的是**哪一個 .pmx**(相對於包根、小寫、<c>/</c> 分隔);
        /// 空 = 沒說,收端自己挑。
        ///
        /// 🔴 <b>一個資料夾可以有好幾個 .pmx,而 packId 是整包的指紋 —— 光憑它認不出是哪一個。</b>
        /// 實測踩過:<c>NerissaRavencroft/</c> 裡有角色本體、一件影子、一把槍,收端照字典序反推挑到那把槍,
        /// 於是「別人畫面上是一支黑色的槍在跳舞」。詳見 <see cref="Sdo.Osu.MmdModelRef"/>。
        ///
        /// 它與 <see cref="MmdName"/> 不同,是**參與判斷**的:換成同一包裡的另一個模型時 packId 一個字都不會變,
        /// 所以 <see cref="Key"/> 與 <see cref="SameAs"/> 都要看它,否則「換了模型但沒人看得到」。
        /// 別人送來的字串一律過 <see cref="Sdo.Osu.MmdModelRef.IsSafeFile"/>(收端會拿它去 Combine 真實路徑)。
        /// </summary>
        public string MmdFile = "";

        /// <summary>
        /// 模型的顯示名稱(資料夾名),純粹給 UI 與 log 看 —— 「正在下載 XXX 的模型」比一串 hash 有用。
        /// **不參與任何判斷**:身分一律看 <see cref="MmdPack"/>,名字是上傳者自己填的、不可信。
        /// </summary>
        public string MmdName = "";

        /// <summary>
        /// 他把模型調到多大(自動對齊舞者身高之後再乘的倍率,＝他那台的 config.ini <c>mmdScale</c>)。
        /// 1 ＝ 只做自動對齊。
        ///
        /// 🔴 <b>大小要跟著人走。</b>「這個模型看起來偏大/偏小」是**穿的人**對他自己那個模型的修正,
        /// 收端沒有任何辦法自己推得出來 —— 少了這個欄位,同一個人在自己畫面上與在別人畫面上就是兩個
        /// 大小。而且頭上名字牌的高度是照畫出來的身高算的(見 <c>MmdHeadroom</c>),所以症狀不只是
        /// 「他矮了一點」,還會是「別人畫面上他的名字插在他頭裡」。
        ///
        /// 它與布料那三根旋鈕(重力/剛性/碰撞半徑)不同 —— 那三根**不上網**:那些是模型自己的東西
        /// (跟著模型包傳過來的 physics.ini),見 <c>MmdTuningPolicy</c>。
        /// </summary>
        public float MmdScale = Sdo.Osu.MmdModelRef.DefaultScale;

        /// <summary>
        /// 「他穿的是哪一個模型、多大」的完整答案(packId ＋ 包內路徑 ＋ 大小),打包成單一字串。
        ///
        /// 專案內部把它當成一個不可分割的值往下搬(房間快照 → 遠端角色 → 頭貼)——
        /// 欄位分開搬的話,漏搬其中一個不會有任何編譯錯誤,而漏掉 <see cref="MmdFile"/> 的症狀
        /// 是「顯示成同一包裡的另一個模型」,漏掉 <see cref="MmdScale"/> 的症狀是「他在別人畫面上
        /// 是另一個大小」—— 兩者都看起來像模型壞掉,而不像少傳了一個欄位。
        /// </summary>
        public string MmdRef() => Sdo.Osu.MmdModelRef.Join(MmdPack, MmdFile, MmdScale);

        /// <summary>模型名稱的長度上限(它會被畫在 UI 上,而且是別人送來的字串)。</summary>
        public const int MaxMmdNameLength = 48;

        public bool Male => Gender == 1;

        public JObj Encode()
        {
            var o = JObj.New().Int("gender", Gender).Int("bodyIndex", BodyIndex);
            if (!string.IsNullOrEmpty(MmdPack)) o.Str("mmd", MmdPack);
            if (!string.IsNullOrEmpty(MmdPack) && !string.IsNullOrEmpty(MmdFile)) o.Str("mmdFile", MmdFile);
            if (!string.IsNullOrEmpty(MmdName)) o.Str("mmdName", MmdName);
            // 沒穿模型 / 沒調大小的人(絕大多數)不該因為這個功能而在每份房間快照裡多帶一個欄位。
            if (!string.IsNullOrEmpty(MmdPack) && !Sdo.Osu.MmdModelRef.IsDefaultScale(MmdScale))
                o.Num("mmdScale", MmdScale);
            var arr = JArr.New();
            if (Parts != null)
            {
                int n = Parts.Length < MaxParts ? Parts.Length : MaxParts;
                for (int i = 0; i < n; i++) arr.Add(Clip(Parts[i]));
            }
            o.Put("parts", arr);
            return o;
        }

        /// <summary>
        /// 解析 + 夾值。**不會失敗** —— 外觀資料壞掉最糟就是角色長得怪,不值得為它斷線
        /// (對比 <see cref="NetSongRef"/>:那個壞掉會讓人跑錯譜面,所以必須拒絕)。
        /// </summary>
        public static NetAvatarLook Decode(object node)
        {
            var look = new NetAvatarLook();
            if (node == null) return look;

            look.Gender = NetJson.Int(node, "gender") == 1 ? 1 : 0;

            // packId 格式不對就當成「沒有 MMD」—— 與這個類別的其它欄位同一個原則:外觀資料壞掉最糟
            // 是角色長得怪,不值得為它斷線。而且不驗的話,一個亂填的 id 會讓每個收到的人都去跟 server
            // 要一份不存在的模型(每進一次房間問一次)。
            string mmd = NetJson.Str(node, "mmd");
            look.MmdPack = Sdo.Osu.SongPackId.IsWellFormed(mmd) ? mmd : "";
            look.MmdName = look.MmdPack.Length == 0 ? "" : ClipName(NetJson.Str(node, "mmdName"));
            // 包內路徑:收端會拿它去 Combine 一個真實資料夾,所以走與傳檔清單同一道安全關。
            // 不合格 → 當成「他沒指定」(收端自己挑),而不是把整個模型丟掉 —— 舊 client 本來就不送這個欄位。
            string mmdFile = Sdo.Osu.SafeRelPath.Normalize(NetJson.Str(node, "mmdFile"));
            look.MmdFile = look.MmdPack.Length != 0 && Sdo.Osu.MmdModelRef.IsSafeFile(mmdFile) ? mmdFile : "";
            // 大小:別人送來的數字一律夾成能用的值(0 / 負數 / 沒帶 → 1×,＝只做自動對齊身高)。
            look.MmdScale = look.MmdPack.Length == 0
                          ? Sdo.Osu.MmdModelRef.DefaultScale
                          : Sdo.Osu.MmdModelRef.ClampScale((float)NetJson.Num(node, "mmdScale"));

            int body = NetJson.Int(node, "bodyIndex");
            look.BodyIndex = body < 0 ? 0 : (body > 4 ? 4 : body);

            var arr = NetJson.Arr(node, "parts");
            if (arr != null && arr.Count > 0)
            {
                int n = arr.Count < MaxParts ? arr.Count : MaxParts;
                var parts = new List<string>(n);
                for (int i = 0; i < n; i++)
                {
                    var s = arr[i] as string;
                    if (!string.IsNullOrEmpty(s)) parts.Add(Clip(s));
                }
                if (parts.Count > 0) look.Parts = parts.ToArray();
            }
            return look;
        }

        /// <summary>
        /// 完整複製一份外觀。
        ///
        /// 🔴 <b>要複製 <see cref="NetAvatarLook"/> 一律走這裡,不要手捲。</b>
        /// 手捲的複製漏掉一個欄位不會有任何編譯錯誤或警告 —— 那個值就是靜靜地不見了。
        /// (實際踩過兩次:<c>PublishLook</c> 手捲 JSON 漏了 MmdPack → 模型上傳一律被
        ///  「這不是你身上穿的那個模型」擋掉;<c>NetMatchPlayerSnapshot.Capture</c> 手捲複製漏了同一個欄位
        ///  → 進遊戲之後每個人的 MMD 模型都變回 SDO 穿搭,而房間裡明明是好的。)
        /// </summary>
        public NetAvatarLook Clone() => new NetAvatarLook
        {
            Gender = Gender,
            BodyIndex = BodyIndex,
            Parts = Parts != null ? (string[])Parts.Clone() : null,
            MmdPack = MmdPack ?? "",
            MmdFile = MmdFile ?? "",
            MmdName = MmdName ?? "",
            MmdScale = MmdScale,
        };

        /// <summary>
        /// 兩份外觀一樣嗎?<c>null</c> 與空陣列視為相等(兩者都代表「用預設整套」)。
        ///
        /// 為什麼需要它:client 有兩個地方要判斷「外觀變了沒」——
        /// ① **送出去重**:每送一次 setLook,server 就 <c>rev++</c> 並向全房廣播一份完整快照,不能白送。
        /// ② **遠端角色要不要重建**:生一隻要讀十幾個部件檔(50-100ms 的 hitch),
        ///    絕不能因為別人按了準備、換了歌之類的 rev 變動就重建。
        /// 兩處必須用同一套規則,所以放在這裡(零 Unity 依賴 → 可單元測試)。
        /// </summary>
        public bool SameAs(NetAvatarLook o)
        {
            if (o == null) return false;
            if (Gender != o.Gender || BodyIndex != o.BodyIndex) return false;
            if (!string.Equals(MmdPack ?? "", o.MmdPack ?? "", System.StringComparison.Ordinal)) return false;
            // 同一包裡換另一個模型 → packId 一個字都不會變,只有這裡看得出來(不比的話「換了沒人看得到」)。
            if (!string.Equals(MmdFile ?? "", o.MmdFile ?? "", System.StringComparison.Ordinal)) return false;
            // 大小同理:改倍率時 packId 與檔名都不會變,不比的話 setLook 會判定「外觀沒變」而不送出,
            // 於是別人畫面上他永遠是舊的大小。
            if (!Sdo.Osu.MmdModelRef.SameScale(MmdScale, o.MmdScale)) return false;
            int a = Parts != null ? Parts.Length : 0;
            int b = o.Parts != null ? o.Parts.Length : 0;
            if (a != b) return false;
            for (int i = 0; i < a; i++)
                if (!string.Equals(Parts[i], o.Parts[i], System.StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// 外觀的比較鍵(給「用字典記住上一次長相」的地方用)。
        /// **順序有意義** —— 部件的疊圖順序會影響外觀,所以不排序。null 與空陣列產生同一個鍵。
        /// </summary>
        public string Key()
        {
            var sb = new System.Text.StringBuilder(64);
            sb.Append(Gender).Append('/').Append(BodyIndex);
            // MMD 模型換了也要讓遠端重建 —— packId、**包內是哪一個 .pmx**、**多大**三個都進鍵
            // (同一包裡換一個模型 / 只改大小,packId 都不會變,少了後兩者畫面上就不會跟著換);
            // 名字不進(名字是顯示用的,它變了畫面上什麼都不會不同,卻會白白重建一隻角色)。
            if (!string.IsNullOrEmpty(MmdPack)) sb.Append('#').Append(MmdRef());
            if (Parts != null)
                for (int i = 0; i < Parts.Length; i++) sb.Append('|').Append(Parts[i] ?? "");
            return sb.ToString();
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= MaxPartNameLength ? s : s.Substring(0, MaxPartNameLength);
        }

        private static string ClipName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= MaxMmdNameLength ? s : s.Substring(0, MaxMmdNameLength);
        }
    }

    /// <summary>
    /// 房間裡的一個座位(共 <see cref="NetLimits.RoomCapacity"/> 個)。
    ///
    /// 對映 client 端既有的 <c>Sdo.UI.Services.SeatInfo</c>,但多了連線才需要的欄位:
    /// <see cref="UserId"/>(server 配的身分,不能用名字當 key —— 名字會重複)、
    /// <see cref="State"/>(多了「被房主關閉」這個狀態)、<see cref="Avail"/>(缺歌顯示)、
    /// <see cref="PlayState"/>(頭貼上的「遊戲中」)。
    /// </summary>
    public sealed class NetSeat
    {
        /// <summary>空著 / 被關閉 / 有人。</summary>
        public SeatState State = SeatState.Open;

        /// <summary>server 配發的使用者 id。空位時是 0。</summary>
        public int UserId;

        /// <summary>顯示名稱。</summary>
        public string Name = "";

        /// <summary>家族名。空 = 沒有家族(頭上名牌不顯示那一行)。</summary>
        public string Guild = "";

        /// <summary>家族徽章名(DATA/EMBLEM,如 <c>SMALL43</c>)。空 = 只顯示家族名、不放徽章。</summary>
        public string GuildEmblem = "";

        /// <summary>等級(頭上名牌的 Lv)。</summary>
        public int Level;

        /// <summary>外觀。</summary>
        public NetAvatarLook Look = new NetAvatarLook();

        /// <summary>
        /// 按了準備。
        ///
        /// 🔴 **房主的這個欄位永遠是 false** —— 房主沒有「準備」這個狀態:它的頭貼位置顯示的是
        /// host 徽章(官方 master.an),而它按的是「開始」不是「準備」。
        /// 判斷「這個座位能不能被納入下一場」要用「是房主 或 Ready」,不要把房主的 Ready 設成 true
        /// (那會讓這個欄位同時承載兩種語意,每個讀它的地方都得記得排除房主)。
        ///
        /// UI 對應:房主那一格畫 <c>master.an</c> 徽章、**不畫準備標記**;其他人畫準備標記。
        /// </summary>
        public bool Ready;

        /// <summary>隊伍 0=A 1=B 2=C 3=自由。</summary>
        public int Team = (int)TeamTag.Free;

        /// <summary>遊玩狀態。</summary>
        public PlayState PlayState = PlayState.Idle;

        /// <summary>有沒有房主選的那首歌。</summary>
        public Availability Avail = Availability.Unknown;

        /// <summary>下載進度 0..1(只在 <see cref="Availability.Downloading"/> 時有意義)。</summary>
        public float AvailProgress;

        public bool IsTaken => State == SeatState.Taken;
        public bool IsOpen => State == SeatState.Open;
        public bool IsClosed => State == SeatState.Closed;

        /// <summary>把座位清成「空著」(玩家離開 / 被踢)。</summary>
        public void Clear()
        {
            State = SeatState.Open;
            UserId = 0;
            Name = "";
            Guild = "";
            GuildEmblem = "";
            Level = 0;
            Look = new NetAvatarLook();
            Ready = false;
            Team = (int)TeamTag.Free;
            PlayState = PlayState.Idle;
            Avail = Availability.Unknown;
            AvailProgress = 0f;
        }

        public JObj Encode()
        {
            var o = JObj.New()
                .Str("state", NetState.ToWire(State))
                .Int("userId", UserId);

            // 空位/關閉的座位不需要帶玩家資料 —— 省掉大部分的 snapshot 體積。
            if (State != SeatState.Taken) return o;

            return o
                .Str("name", Name)
                .Str("guild", Guild)
                .Str("guildEmblem", GuildEmblem)
                .Int("level", Level)
                .Put("look", Look != null ? Look.Encode() : null)
                .Bool("ready", Ready)
                .Int("team", Team)
                .Str("playState", NetState.ToWire(PlayState))
                .Str("avail", NetState.ToWire(Avail))
                .Num("availProgress", AvailProgress);
        }

        /// <summary>解析(寬鬆:壞掉的座位資料退成空位,不斷線)。</summary>
        public static NetSeat Decode(object node)
        {
            var s = new NetSeat();
            if (node == null) return s;

            SeatState st;
            if (!NetState.TryParseSeatState(NetJson.Str(node, "state"), out st)) st = SeatState.Open;
            s.State = st;
            s.UserId = NetJson.Int(node, "userId");

            if (s.State != SeatState.Taken) { s.UserId = 0; return s; }

            s.Name = NetJson.Str(node, "name");
            s.Guild = NetJson.Str(node, "guild");
            s.GuildEmblem = NetJson.Str(node, "guildEmblem");
            s.Level = NetJson.Int(node, "level");
            s.Look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));
            s.Ready = NetJson.Bool(node, "ready");
            s.Team = (int)NetState.ClampTeam(NetJson.Int(node, "team", (int)TeamTag.Free));

            PlayState ps;
            if (!NetState.TryParsePlayState(NetJson.Str(node, "playState"), out ps)) ps = PlayState.Idle;
            s.PlayState = ps;

            Availability av;
            if (!NetState.TryParseAvailability(NetJson.Str(node, "avail"), out av)) av = Availability.Unknown;
            s.Avail = av;

            float p = (float)NetJson.Num(node, "availProgress");
            s.AvailProgress = p < 0f ? 0f : (p > 1f ? 1f : p);

            return s;
        }
    }

    /// <summary>
    /// 旁觀者。**不佔座位** —— 房間可以有 6 個舞者 + 最多
    /// <see cref="NetLimits.MaxSpectators"/> 個旁觀者(對應官方 EXE 的 10 個旁觀座標)。
    /// </summary>
    public sealed class NetSpectator
    {
        public int UserId;
        public string Name = "";

        /// <summary>家族名 / 徽章。旁觀者一樣站在房間裡、頭上一樣有名字牌 ——
        /// 沒有這兩個欄位的話,同一個人從座位換到旁觀席,家族列就會憑空消失。</summary>
        public string Guild = "", GuildEmblem = "";

        public int Level;
        public NetAvatarLook Look = new NetAvatarLook();

        /// <summary>
        /// 旁觀者也要上報有沒有這首歌 —— 有歌的旁觀者才會跟著進打歌畫面(看別人跳舞),
        /// 缺歌的留在房間。**旁觀者不自動下載**(使用者要求)。
        /// </summary>
        public Availability Avail = Availability.Unknown;

        public JObj Encode()
            => JObj.New()
                .Int("userId", UserId)
                .Str("name", Name)
                .Str("guild", Guild)
                .Str("guildEmblem", GuildEmblem)
                .Int("level", Level)
                .Put("look", Look != null ? Look.Encode() : null)
                .Str("avail", NetState.ToWire(Avail));

        public static NetSpectator Decode(object node)
        {
            var s = new NetSpectator();
            if (node == null) return s;
            s.UserId = NetJson.Int(node, "userId");
            s.Name = NetJson.Str(node, "name");
            s.Guild = NetJson.Str(node, "guild");
            s.GuildEmblem = NetJson.Str(node, "guildEmblem");
            s.Level = NetJson.Int(node, "level");
            s.Look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));

            Availability av;
            if (!NetState.TryParseAvailability(NetJson.Str(node, "avail"), out av)) av = Availability.Unknown;
            s.Avail = av;
            return s;
        }
    }
}
