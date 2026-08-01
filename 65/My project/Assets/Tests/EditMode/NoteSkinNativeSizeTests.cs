using System.IO;
using System.Reflection;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 守住「官方 2D noteskin 一律 1:1 原生像素繪製」這條規則（使用者實機比對回報「unity 的 note 比官方小一點」的修正）。
    ///
    /// 考據來源：
    ///   ① 每一套 4-key skin（NOTEIMAGE_5/6/8/9/10/11/PET/SHOWTIME）的圖尺寸完全一致 —
    ///      *HOLDHEADACTIVE* = 100×80（落下音符 4 幀）、*_JUDGELINE* = 100×100（受擊區）、*_LONG* = 100×64（長條 body）。
    ///   ② 反編譯 <c>NewNote_DrawNoteWithEffects_004909c0</c>：目的高度取自 source rect（單位＝貼圖像素）、旋轉中心取
    ///      <c>Pic_GetWidth/GetHeight</c> 的一半 → dest 尺寸 == 貼圖原生尺寸，全程沒有縮放係數；跟 NOTES_BOARD1
    ///      315×600 必須 1:1 是同一條規則（見 docs/reverse-engineering/SDO_NOTE_BOARD.md）。
    ///
    /// 舊值 NoteW=82 / ReceptorW=92 是目測估的，箭頭實體只畫到 56×0.82≈46px，比官方小 18%。
    /// 貼圖 100 寬 > 車道 pitch 69 是故意的：箭頭實體只佔 x[22..77]≈56px，外圈是給旋轉/擺動特效留的透明 padding。
    ///
    /// 貼圖檢查讀真實資料（沒資料就 Ignore，比照 <see cref="MipButtonTextureTests"/>）；常數檢查是純反射，永遠會跑。
    /// </summary>
    public class NoteSkinNativeSizeTests
    {
        // 官方 4-key noteskin 的原生圖尺寸（每一套都一樣）。
        const int SkinTexW = 100, NoteTexH = 80, JudgeTexH = 100, LongTexH = 64;
        // 3D skin（hiteft3D）畫的是 mesh 不是這組貼圖，尺寸另外校準 —— 它的基準寬不該跟著 2D 的 1:1 修正動。
        const float Note3dReceptorPx = 61.5f;   // 3D 受擊區精靈的目標顯示寬（見 receptor3dScale 的推導註解）

        static string NoteImageDir()
        {
            foreach (var d in new[] { Path.Combine(SdoExtracted.Root, "NOTEIMAGE"), @"H:/65_remake_clean/DATA/NOTEIMAGE" })
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            return null;
        }

        /// <summary>從 PNG 的 IHDR 直接讀寬高 —— 不經 Unity 匯入，量到的就是檔案裡的原生尺寸。</summary>
        static bool PngSize(string path, out int w, out int h)
        {
            w = h = 0;
            if (!File.Exists(path)) return false;
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[24];
                if (fs.Read(b, 0, 24) != 24) return false;
                if (b[0] != 0x89 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G') return false;
                w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];   // IHDR width  (big-endian)
                h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];   // IHDR height
                return true;
            }
        }

        static float Const(string name)
        {
            var f = typeof(ScreenGameplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(f, $"ScreenGameplay.{name} 常數不見了（被改名/刪掉的話這條 1:1 規則就沒人守了）");
            return (float)f.GetRawConstantValue();
        }

        // ── ① 2D 音符 / 受擊區 的顯示寬 == 貼圖原生寬（1:1，不縮放）─────────────────────────────────────────
        [Test]
        public void NoteAndReceptorWidths_AreTheNativeTextureWidth()
        {
            Assert.AreEqual(SkinTexW, Const("NoteW"), 0.001f,
                "2D 落下音符/炸彈/長條要以貼圖原生寬 100 繪製（官方 1:1）；調小會讓箭頭比官方細一圈");
            Assert.AreEqual(SkinTexW, Const("ReceptorW"), 0.001f,
                "受擊區 JUDGELINE 也是 100×100，同樣 1:1；官方 receptor 圖案本來就比音符略小(54 vs 56)，是圖畫的不是縮放");
        }

        // ── ② 2D 長條 tile 不變形：holdW=NoteW 時，tileH 必須剛好等於 *_LONG 的原生高 ───────────────────────
        [Test]
        public void HoldBodyTile_IsUnstretchedAtNativeWidth()
        {
            // ScrollNotes 的 2D 分支：tileH = holdW × (texH / texW)。holdW == texW 時 tileH == texH，圖樣 1:1 不變形。
            float tileH = Const("NoteW") * (LongTexH / (float)SkinTexW);
            Assert.AreEqual(LongTexH, tileH, 0.001f,
                "2D 長條是 tile 不是拉伸：寬度偏離貼圖原生寬時，V 方向的節距會跟著縮，chevron 圖樣比例就跑掉");
        }

        // ── ③ 3D skin 不吃這次的 2D 修正（它畫 mesh，基準另外校準過）────────────────────────────────────────
        [Test]
        public void ThreeDSkinKeepsItsOwnCalibratedBaseWidth()
        {
            Assert.AreEqual(82f, Const("Note3dBaseW"), 0.001f,
                "3D skin 的基準寬是照 NOTES.MSH 幾何校準的 82，不該被 2D 的 1:1 修正連動");
            // 3D 受擊區：精靈寬 = 音符寬 ÷ 0.9731（mesh 只覆蓋 128px JUDGELINE 精靈的 97.31%）。
            // ReceptorW 一旦改動，receptor3dScale 必須跟著重新標定，否則 3D 判定區會整個跟著縮放。
            float px = Const("Receptor3dScaleDefault") * Const("ReceptorW");
            Assert.AreEqual(Note3dReceptorPx, px, 0.2f,
                "receptor3dScale × ReceptorW 要維持 3D 受擊區 ≈61.5px；改 ReceptorW 時忘了重標定就會踩到這裡");
        }

        // ── ④ 真實資料：每一套 4-key skin 的圖尺寸都是 100×80 / 100×100 / 100×64 ────────────────────────────
        [Test]
        public void EverySkinShipsTheSameNativeTextureSizes()
        {
            var root = NoteImageDir();
            if (root == null) Assert.Ignore("NOTEIMAGE art not present in this environment.");

            string[] skins = { "NOTEIMAGE_5", "NOTEIMAGE_6", "NOTEIMAGE_8", "NOTEIMAGE_9",
                               "NOTEIMAGE_10", "NOTEIMAGE_11", "NOTEIMAGE_PET", "NOTEIMAGE_SHOWTIME" };
            int checkedSkins = 0;
            foreach (var skin in skins)
            {
                var dir = Path.Combine(root, skin);
                if (!Directory.Exists(dir)) continue;
                checkedSkins++;

                foreach (var lane in new[] { "up", "down", "left", "right" })
                {
                    AssertPng(dir, lane + "holdheadactive0.png", SkinTexW, NoteTexH, skin);
                    // receptor 命名兩套：NOTEIMAGE_5/6 是 *_judgeline1，其餘是 *_judgeline
                    if (!TryAssertPng(dir, lane + "_judgeline1.png", SkinTexW, JudgeTexH, skin))
                        AssertPng(dir, lane + "_judgeline.png", SkinTexW, JudgeTexH, skin);
                }
                AssertPng(dir, "updown_long.png", SkinTexW, LongTexH, skin);
                AssertPng(dir, "rightleft_long.png", SkinTexW, LongTexH, skin);
            }
            Assert.Greater(checkedSkins, 0, "一套 noteskin 都沒找到 — 資料樹不完整");
        }

        static bool TryAssertPng(string dir, string file, int w, int h, string skin)
        {
            int gw, gh;
            if (!PngSize(Path.Combine(dir, file), out gw, out gh)) return false;
            Assert.AreEqual(w, gw, $"{skin}/{file} 寬度");
            Assert.AreEqual(h, gh, $"{skin}/{file} 高度");
            return true;
        }

        static void AssertPng(string dir, string file, int w, int h, string skin)
        {
            Assert.IsTrue(TryAssertPng(dir, file, w, h, skin), $"{skin}/{file} 不存在或不是 PNG");
        }
    }
}
