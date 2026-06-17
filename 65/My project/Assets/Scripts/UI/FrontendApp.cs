using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Screens;
using Sdo.UI.Util;

namespace Sdo.UI
{
    /// <summary>
    /// Front-end entry point. Self-boots (RuntimeInitialize), takes over from the gameplay scene by
    /// destroying any auto-spawned Step1Game (runs first via a very low execution order — zero edits
    /// to Step1Game), builds the canvas + screens + modals procedurally, and drives the flow.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class FrontendApp : MonoBehaviour
    {
        public static FrontendApp Instance { get; private set; }

        private AppContext _ctx;
        private readonly Dictionary<ScreenId, UIScreenBase> _screens = new Dictionary<ScreenId, UIScreenBase>();
        private SettingsModal _settings;
        private NoteSkinPicker _notePicker;
        private int _killGuardFrames = 3;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("FrontendApp");
            Instance = go.AddComponent<FrontendApp>();
            DontDestroyOnLoad(go);
        }

        private void Start()
        {
            KillStrayGameplay();

            var lang = LanguageInfo.FromCode(DisplaySettingsManager.Settings?.language);
            LocalizationManager.Init(lang);

            _ctx = AppContext.CreateMock();

            var canvas = UIKit.CreateCanvas("FrontendCanvas", new Vector2(1280, 720), 0);
            var root = (RectTransform)canvas.transform;
            UIKit.Stretch(UIKit.AddImage(root, "AppBg", UITheme.Bg).rectTransform);

            var screenLayer = UIKit.NewRect(root, "Screens");
            UIKit.Stretch(screenLayer);
            Make<LobbyScreen>(screenLayer);
            Make<RoomScreen>(screenLayer);
            Make<SongSelectScreen>(screenLayer);

            _ctx.Flow.ScreenChanged += (from, to) => ShowOnly(to);

            var modalLayer = UIKit.NewRect(root, "Modals");
            UIKit.Stretch(modalLayer);
            _settings = new GameObject("Settings").AddComponent<SettingsModal>();
            _settings.transform.SetParent(modalLayer, false);
            _settings.Build(modalLayer);
            _notePicker = new GameObject("NotePicker").AddComponent<NoteSkinPicker>();
            _notePicker.transform.SetParent(modalLayer, false);
            _notePicker.Build(modalLayer, _ctx.Session);
            Toast.Init(modalLayer);

            Nav.OpenSettings = () => _settings.Open();
            Nav.OpenNoteSkinPicker = () => _notePicker.Open();
            Nav.StartGameStub = () => Toast.Show(LocalizationManager.Get("room.start_stub"));

            WarmupFont();
            ShowOnly(_ctx.Flow.Current);
        }

        private void Update()
        {
            _ctx?.Chat?.Tick();
            if (_killGuardFrames > 0) { _killGuardFrames--; KillStrayGameplay(); }
        }

        private void Make<T>(RectTransform parent) where T : UIScreenBase
        {
            var rt = UIKit.NewRect(parent, typeof(T).Name);
            UIKit.Stretch(rt);
            var screen = rt.gameObject.AddComponent<T>();
            screen.Build(_ctx);
            _screens[screen.Id] = screen;
        }

        private void ShowOnly(ScreenId id)
        {
            foreach (var kv in _screens)
                kv.Value.SetVisible(kv.Key == id);
        }

        private static void KillStrayGameplay()
        {
            // The committed Step1Game self-boots into any scene; the front-end is the entry point in
            // this build, so remove it. (Gameplay hand-off is added in M5, after the burst-opt merge.)
            foreach (var g in FindObjectsByType<Step1Game>(FindObjectsSortMode.None))
                Destroy(g.gameObject);
        }

        private void WarmupFont()
        {
            var sb = new StringBuilder();
            sb.Append("0123456789%×♪★✓●◀▶ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
            int budget = 4000;
            foreach (var e in SongCatalog.All)
            {
                if (budget <= 0) break;
                if (!string.IsNullOrEmpty(e.title)) { sb.Append(e.title); budget -= e.title.Length; }
                if (!string.IsNullOrEmpty(e.artist)) { sb.Append(e.artist); budget -= e.artist.Length; }
            }
            UIFont.Warmup(sb.ToString());
        }
    }
}
