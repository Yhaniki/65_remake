using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 「遊戲視角」偏好 ↔ 遊戲內鏡頭模式（ScreenGameplay._camMode：-1=自動導播 / 0..n-1=固定鏡頭）的純函式轉換。
    /// 這是遊戲中按 F2 換鏡頭會被記住的關鍵：F2 切到固定 → OPTION 標籤跟著變「固定」且記住是第幾台，
    /// 下一局開場直接鎖那台；F2 循環回自動 → 變回「默認」，但台號保留。
    /// </summary>
    public class CameraViewPrefTests
    {
        private const int N = 6;   // = ScreenGameplay.FixedCamCount（6 台固定鏡頭）

        [Test]
        public void Default_Is_AutoDirector()
        {
            var g = new GameplaySettings();
            Assert.IsTrue(g.cameraAuto);
            Assert.AreEqual(GameplaySettings.AutoCamMode, g.ToCamMode(N));
        }

        [Test]
        public void F2_To_Fixed_Cam_Is_Remembered_And_Flips_Label_To_Fixed()
        {
            var g = new GameplaySettings();
            Assert.IsTrue(g.SetFromCamMode(3, N), "值有變 → 要存檔");
            Assert.IsFalse(g.cameraAuto, "切到固定鏡頭 → OPTION 標籤要變「固定」");
            Assert.AreEqual(3, g.cameraFixed);
            Assert.AreEqual(3, g.ToCamMode(N), "下一局開場鎖同一台");
        }

        [Test]
        public void F2_Back_To_Auto_Restores_Label_But_Keeps_Last_Fixed_Cam()
        {
            var g = new GameplaySettings();
            g.SetFromCamMode(4, N);
            Assert.IsTrue(g.SetFromCamMode(GameplaySettings.AutoCamMode, N));
            Assert.IsTrue(g.cameraAuto, "循環回自動導播 → 標籤變回「默認」");
            Assert.AreEqual(4, g.cameraFixed, "台號保留（下次選「固定」還是這一台）");
            Assert.AreEqual(GameplaySettings.AutoCamMode, g.ToCamMode(N));
        }

        [Test]
        public void No_Change_Reports_False_So_Caller_Skips_The_Write()
        {
            var g = new GameplaySettings();
            Assert.IsFalse(g.SetFromCamMode(GameplaySettings.AutoCamMode, N), "本來就是自動 → 不用寫檔");
            g.SetFromCamMode(2, N);
            Assert.IsFalse(g.SetFromCamMode(2, N), "同一台 → 不用寫檔");
        }

        [Test]
        public void Out_Of_Range_Fixed_Index_Is_Clamped()
        {
            var g = new GameplaySettings { cameraAuto = false, cameraFixed = 99 };
            Assert.AreEqual(N - 1, g.ToCamMode(N), "手改壞的 config 不能讓開局鏡頭索引越界");

            var h = new GameplaySettings();
            h.SetFromCamMode(99, N);
            Assert.AreEqual(N - 1, h.cameraFixed);
        }
    }
}
