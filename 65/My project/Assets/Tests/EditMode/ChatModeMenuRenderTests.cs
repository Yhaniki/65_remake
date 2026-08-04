using System.IO;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 遊戲中聊天框的 chatmode 彈出選單(家族/好友/當前/回復)—— **真的畫出來讀像素**,不是只算數學。
    ///
    /// 使用者回報「官方那四顆按鈕間距一樣,unity 上的間距很亂」。根因是「當前」那張圖(ChatROOM 的 Room4,
    /// 51×30)四周比其餘三張(SmallButton 的 49×25)多留了透明邊,照 sprite 矩形每 25px 排就會在
    /// 「好友↔當前」之間開一道縫、在「當前↔回復」之間互相疊,而且整顆往右突 1px。官方自己的
    /// <c>GAMEPLAYNEWLEAN.XML</c> 為條上同一顆鈕寫了三行(chatmode y=565 / friendchatmode /
    /// Familychatmode y=568),差的就是那 3px。
    ///
    /// 幾何斷言在 <see cref="GameplayChatLayoutTests"/>;這裡補的是「畫出來確實是等距的」——
    /// 把四張**真素材**(走 <see cref="SdoExtracted.ChatArt"/> 的完整 crop / cleanMatte / premultiply 管線)
    /// 依正式版位擺好,拿放大的正交相機拍在**亮綠底**上,再從「背景可見度」的縱向剖面量四顆的視覺頂緣。
    /// 對照組用修正前的偏移擺同一批圖,必須量得到參差 —— 否則這個探針只是碰巧綠燈。
    /// 沒有遊戲資料的環境自動跳過;SDO_SHOT_DIR 有設就順手 dump PNG 供人眼複驗。
    /// </summary>
    public class ChatModeMenuRenderTests
    {
        private const int Zoom = 6;                                          // design px → render px
        private static readonly Color Backdrop = new Color(0f, 1f, 0f, 1f);  // 按鈕是紫的,綠底最好分

        /// <summary>選單四顆由上而下的 normal 態素材(＝GameplayChat.ModeArt 各自的第 0 張)。</summary>
        private static readonly string[] MenuArt = { "Room203.an", "Room200.an", "Room4.an", "Room206.an" };
        /// <summary>條上那顆 chatmode 鈕(預設頻道「當前」)—— 它是這一柱的第 5 顆,官方五顆同一個間距。</summary>
        private const int BarButtonChannel = 2;

        [Test]
        public void Five_Buttons_Are_Evenly_Spaced_On_Screen()
        {
            var m = Measure(useOfficialPads: true, "chatmode-menu.png");
            TestContext.Out.WriteLine("修正後 " + m);
            // 使用者的原話是「官方是全部五個都同樣間格」—— 量的就是這個:選單四顆**加上條上那顆**,
            // 相鄰兩顆的視覺頂緣一律差官方槽距 25。
            // 容差 1.2:「當前」那張圖底部兩列柔邊(alpha 48~64)太淡,它下面那一段的半高點會被往下拉 ~1px,
            // 不是版位跑掉(對照組差得出 3px)。
            for (int i = 0; i < m.Pitch.Length; i++)
                Assert.AreEqual(GameplayChatLayout.ModeArtVisualH, m.Pitch[i], 1.2f,
                    $"第 {i + 1} 段間距是 {m.Pitch[i]:F2} design px,不是官方的 25 —— 這一柱又參差了:{m}");
            // 縫也不准大到看得出來。1.6 的上限是「當前」那張圖底部兩列柔邊(alpha 48~64)撐出來的,
            // 素材天生如此(它的視覺高度比另外三張多一列),官方對齊頂緣也躲不掉;超過就是版位又跑掉了。
            foreach (float g in m.Gap)
                Assert.LessOrEqual(g, 1.6f, "接縫漏出來的背景變寬了:" + m);
        }

        [Test]
        public void Ignoring_The_Transparent_Border_Makes_Them_Uneven_Control()
        {
            // 對照組:不補「當前」那張多留的透明邊(＝使用者截圖裡的狀態)。「好友↔當前」必須量得到參差 + 縫,
            // 否則上面那個測試是為了錯誤的理由通過(相機沒對準 / 圖根本沒畫出來)。
            var m = Measure(useOfficialPads: false, "chatmode-menu-raw.png");
            TestContext.Out.WriteLine("不補透明邊 " + m);
            Assert.Greater(m.Pitch[1], GameplayChatLayout.ModeArtVisualH + 1.5f,
                "對照組的『好友↔當前』間距沒有變大 — 這個量測失去鑑別力了:" + m);
            Assert.Greater(m.Gap[1], 1.5f,
                "對照組的『好友↔當前』沒有露出背景 — 這個量測失去鑑別力了:" + m);
        }

        /// <summary>四顆在螢幕上的實測結果:相鄰視覺頂緣的間距(3 段)＋各接縫漏出的背景寬度(3 道)。</summary>
        /// <summary>由上而下五顆的名字:選單四顆 + 條上那顆。</summary>
        private static readonly string[] Names = { "家族", "好友", "當前", "回復", "條上的當前" };

        private struct Measured
        {
            public float[] Pitch, Gap, Edge;
            public override string ToString()
            {
                var s = new System.Text.StringBuilder("間距");
                for (int i = 0; i < Pitch.Length; i++)
                    s.Append($" {Names[i]}→{Names[i + 1]} {Pitch[i]:F2}");
                s.Append(" design px、縫");
                for (int i = 0; i < Gap.Length; i++) s.Append($" {Gap[i]:F2}");
                s.Append("(視覺頂緣");
                foreach (float e in Edge) s.Append($" {e:F2}");
                return s.Append(')').ToString();
            }
        }

        private static Measured Measure(bool useOfficialPads, string dumpName)
        {
            var art = new Sprite[MenuArt.Length];
            for (int i = 0; i < MenuArt.Length; i++)
            {
                art[i] = SdoExtracted.ChatArt(MenuArt[i]);
                if (art[i] == null) Assert.Ignore("PLAYNEWLEAN art not present in this environment.");
            }

            var layout = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            float top = layout.ModeMenuTopY;
            float menuX = layout.BarItemX(GameplayChatLayout.ModeBtnDx);

            // 五個槽位:選單四顆 + 條上那顆 chatmode 鈕。官方是同一個間距,所以一起量。
            var slots = new float[5];
            var channel = new int[5];
            for (int i = 0; i < 4; i++) { slots[i] = top + GameplayChatLayout.ModeMenuSlotY[i]; channel[i] = i; }
            slots[4] = layout.BarItemY(GameplayChatLayout.ModeBtnDy);
            channel[4] = BarButtonChannel;

            var made = new GameObject[slots.Length];
            GameObject camGo = null;
            Texture2D shot = null;
            try
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    var sprite = art[channel[i]];
                    var go = new GameObject("mode" + i);
                    made[i] = go;
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = sprite;
                    var tex = sprite.texture;
                    if (tex != null && SdoExtracted.IsPremultTexture(tex))
                    {
                        var mat = SdoExtracted.PremultSpriteMaterial(tex);
                        if (mat != null) sr.sharedMaterial = mat;
                    }
                    float x = menuX, y = slots[i];
                    if (useOfficialPads)
                        GameplayChatLayout.ModeArtTopLeft(channel[i], menuX, slots[i], out x, out y);
                    SdoLayout.PlaceTopLeft(sr, x, y);
                }

                // 這一柱前後各留 6px 空白,好讓最上面那顆有「純背景」可以當參考
                float columnH = slots[4] + GameplayChatLayout.ModeArtVisualH - slots[0];
                var view = new Rect(menuX - 6f, slots[0] - 6f, GameplayChatLayout.ModeArtVisualW + 12f,
                                    columnH + 12f);
                (shot, camGo) = Photograph(view, dumpName);

                var profile = BackdropProfile(shot);
                var edge = new float[slots.Length];
                var gap = new float[slots.Length - 1];
                for (int i = 0; i < slots.Length; i++)
                {
                    edge[i] = TopEdgeNear(profile, view, slots[i]);
                    if (i > 0) gap[i - 1] = LeakRunAround(profile, view, slots[i]);
                }
                var pitch = new float[slots.Length - 1];
                for (int i = 0; i < pitch.Length; i++) pitch[i] = edge[i + 1] - edge[i];   // design y 往下遞增
                return new Measured { Pitch = pitch, Gap = gap, Edge = edge };
            }
            finally
            {
                if (shot != null) Object.DestroyImmediate(shot);
                if (camGo != null) Object.DestroyImmediate(camGo);
                foreach (var go in made) if (go != null) Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// 每一列的「背景可見度」0..1(1 = 整列都是沒被蓋住的綠底,0 = 整列都是按鈕)。
        /// 用綠減紅藍是因為按鈕是紫的(r、b 高 g 低),半透明的柔邊會落在中間 —— 正是要拿來找邊的。
        /// 左右各縮 3 design px 避開圓角。
        /// </summary>
        private static float[] BackdropProfile(Texture2D shot)
        {
            int inset = (6 + 3) * Zoom;   // view 左右各多留的 6px + 圓角 3px
            int x0 = Mathf.Clamp(inset, 0, shot.width - 1);
            int x1 = Mathf.Clamp(shot.width - inset, x0 + 1, shot.width);
            var p = new float[shot.height];
            for (int py = 0; py < shot.height; py++)
            {
                float sum = 0f;
                for (int px = x0; px < x1; px++)
                {
                    var c = shot.GetPixel(px, py);
                    sum += Mathf.Clamp01(c.g - Mathf.Max(c.r, c.b));
                }
                p[py] = sum / (x1 - x0);
            }
            return p;
        }

        /// <summary>
        /// 某顆按鈕的**視覺頂緣** design y —— 在官方槽位附近找背景可見度的下降半高點。
        ///
        /// 四顆用同一個定義才比得出「間距一樣」:最上面那顆上方是純背景(可見度 1),中間三顆上方是接縫
        /// (可見度是一個小峰),兩種情況都以「峰值的一半」當邊 —— 頂柔邊的剖面四張圖一致,所以半高點落點一致。
        /// (不能改用「第一個不是純背景的列」:按鈕相接時中間根本沒有背景可分隔,那樣只會量到搜尋窗的上緣。)
        /// </summary>
        private static float TopEdgeNear(float[] profile, Rect view, float slotDesignY)
        {
            const float Window = 5f;
            int hi = DesignYToRow(view, slotDesignY - Window, profile.Length);   // 螢幕上比較高 → row 比較大
            int lo = DesignYToRow(view, slotDesignY + Window, profile.Length);
            // 先找到峰在哪 —— 不能從窗頂直接往下找半高點:窗頂本來就落在上一顆的身體上(可見度 0),
            // 第一個列就「低於半高」,量到的會是搜尋窗的上緣而不是按鈕的邊。
            int peakRow = hi;
            for (int py = hi; py >= lo; py--) if (profile[py] > profile[peakRow]) peakRow = py;
            float half = profile[peakRow] * 0.5f;
            for (int py = peakRow; py >= lo; py--)
                if (profile[py] < half)
                    return view.yMin + (profile.Length - 1 - py) / (float)Zoom;
            return slotDesignY + Window;   // 這一段整片都是背景(按鈕沒畫出來)→ 讓上層的斷言去炸
        }

        /// <summary>接縫附近(±4 design px)連續「整列都是背景」的高度,換算回 design px。</summary>
        private static float LeakRunAround(float[] profile, Rect view, float seamDesignY)
        {
            const float Window = 4f;
            int lo = DesignYToRow(view, seamDesignY + Window, profile.Length);
            int hi = DesignYToRow(view, seamDesignY - Window, profile.Length);
            int run = 0, best = 0;
            for (int py = lo; py <= hi; py++)
            {
                if (profile[py] > 0.6f) { run++; if (run > best) best = run; }
                else run = 0;
            }
            return best / (float)Zoom;
        }

        private static int DesignYToRow(Rect view, float dy, int rows)
            => Mathf.Clamp(rows - 1 - Mathf.RoundToInt((dy - view.yMin) * Zoom), 0, rows - 1);

        private static (Texture2D, GameObject) Photograph(Rect view, string dumpName)
        {
            int rw = Mathf.RoundToInt(view.width) * Zoom, rh = Mathf.RoundToInt(view.height) * Zoom;
            var rt = new RenderTexture(rw, rh, 16, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("ChatModeMenuProbeCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = view.height / 2f;
            cam.transform.position = new Vector3(SdoLayout.WorldX(view.center.x), SdoLayout.WorldY(view.center.y), -100f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Backdrop;
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, rw, rh), 0, 0);
            shot.Apply(false);
            RenderTexture.active = prev;
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);

            string dir = System.Environment.GetEnvironmentVariable("SDO_SHOT_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, dumpName), shot.EncodeToPNG());
            }
            return (shot, camGo);
        }
    }
}
