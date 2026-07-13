using System.IO;
using UnityEditor;
using UnityEngine;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Screens;
using Sdo.UI.Util;

namespace Sdo.EditorTools
{
    /// <summary>
    /// 不進 Play mode 也能把「個人資訊/戰績面板」畫出來存成 PNG —— 調版面座標時用（改常數 → 跑一次 → 看圖）。
    ///
    /// 選單：Tools ▸ SDO ▸ Capture PlayerInfo Panel（存到 &lt;repo&gt;/playerinfo-capture.png）
    /// Headless：
    ///   Unity.exe -batchmode -quit -projectPath "&lt;proj&gt;" -executeMethod Sdo.EditorTools.PlayerInfoCapture.CaptureBatch
    ///   （輸出路徑可用 env SDO_CAPTURE_OUT 覆寫）
    ///
    /// 面板的開場動畫 (WindowAnim) 靠 coroutine 推進，EditMode 沒有 Update → 這裡直接 ResetOpen() 定格在全開狀態。
    /// </summary>
    public static class PlayerInfoCapture
    {
        private const int W = 800, H = 600;
        private const int PreviewLayer = 12;   // 3D 預覽人物的層（要從 UI 相機遮掉，否則會直接畫在畫面上）

        [MenuItem("Tools/SDO/Capture PlayerInfo Panel")]
        public static void CaptureMenu() => Capture(DefaultOut());

        public static void CaptureBatch()
        {
            var o = System.Environment.GetEnvironmentVariable("SDO_CAPTURE_OUT");
            Capture(string.IsNullOrEmpty(o) ? DefaultOut() : o);
        }

        private static string DefaultOut() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "playerinfo-capture.png"));

        private static void Capture(string outPath)
        {
            if (!PlayerInfoArt.Available)
            {
                Debug.LogError("[playerinfo-capture] UI/PLAYERINFORMATIONDLG 不在 data root — 先跑 tools/link_data_root.ps1");
                return;
            }

            // 自己搭 canvas + 正交相機（不用 UIKit.CreateWorldCanvas：它掛的 AspectController 會呼叫
            // DontDestroyOnLoad，EditMode 不准）。800×600 world canvas + orthographicSize 300 → 1px = 1 unit。
            var camGo = new GameObject("CaptureCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = H / 2f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.15f, 1f);
            cam.cullingMask = ~(1 << PreviewLayer);   // 預覽人物只能經 RT 進來，不能被主相機直接拍到

            var canvasGo = new GameObject("CaptureCanvas", typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler),
                typeof(UnityEngine.UI.GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            var root = (RectTransform)canvas.transform;
            root.sizeDelta = new Vector2(W, H);
            root.position = Vector3.zero;

            var modalGo = new GameObject("PlayerInfoModal");
            modalGo.transform.SetParent(root, false);
            var modal = modalGo.AddComponent<PlayerInfoModal>();
            modal.Build(root);
            modal.Open(SampleTarget());

            // EditMode 沒有 Update → 開場動畫(WindowAnim)只跑到起始幀(縮小+旋轉)就卡住 → 直接定格在全開。
            // 注意 modal.Build 是把 UI 建在 root 底下（不是 modalGo 底下），所以要從 root 找。
            foreach (var a in root.GetComponentsInChildren<WindowAnim>(true)) a.ResetOpen();

            // 3D 預覽相機在 EditMode 不會被 game loop 驅動 → 手動 render 一次，RawImage 才有東西
#pragma warning disable 0618
            foreach (var c in Object.FindObjectsOfType<Camera>(true))
#pragma warning restore 0618
                if (c != cam && c.targetTexture != null && c.name == "PlayerInfoPreviewCam") c.Render();

            Canvas.ForceUpdateCanvases();

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log("[playerinfo-capture] → " + outPath);

            Object.DestroyImmediate(canvas.gameObject);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }

        // 一組看得出比率差異的樣本（不是真存檔，純截圖用）
        private static PlayerInfoTarget SampleTarget()
        {
            var st = new PlayerStats();
            st.AddGame(perfect: 620, cool: 240, bad: 90, miss: 50, won: true, score: 152340);
            for (int i = 0; i < 9; i++) st.AddGame(600, 260, 90, 50, won: i < 3, score: 120000 + i * 1000);
            return new PlayerInfoTarget { Name = "玩家001", Level = 27, Male = false, IsSelf = true, Stats = st };
        }
    }
}
