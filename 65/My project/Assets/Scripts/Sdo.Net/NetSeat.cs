using System.Collections.Generic;

namespace Sdo.Net
{
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

        public bool Male => Gender == 1;

        public JObj Encode()
        {
            var o = JObj.New().Int("gender", Gender).Int("bodyIndex", BodyIndex);
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

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= MaxPartNameLength ? s : s.Substring(0, MaxPartNameLength);
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

        /// <summary>等級(頭上名牌的 Lv)。</summary>
        public int Level;

        /// <summary>外觀。</summary>
        public NetAvatarLook Look = new NetAvatarLook();

        /// <summary>按了準備。host 恆為 true(它不需要準備)。</summary>
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
                .Int("level", Level)
                .Put("look", Look != null ? Look.Encode() : null)
                .Str("avail", NetState.ToWire(Avail));

        public static NetSpectator Decode(object node)
        {
            var s = new NetSpectator();
            if (node == null) return s;
            s.UserId = NetJson.Int(node, "userId");
            s.Name = NetJson.Str(node, "name");
            s.Level = NetJson.Int(node, "level");
            s.Look = NetAvatarLook.Decode(NetJson.Sub(node, "look"));

            Availability av;
            if (!NetState.TryParseAvailability(NetJson.Str(node, "avail"), out av)) av = Availability.Unknown;
            s.Avail = av;
            return s;
        }
    }
}
