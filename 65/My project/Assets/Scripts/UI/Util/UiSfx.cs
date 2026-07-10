using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Front-end sound-effect player for the shipped SE/*.wav (loaded from <see cref="SdoExtracted.SeDir"/>,
    /// cached, one-shot). Mirrors ScreenGameplay's SE playback but lives in the front-end so dialogs and buttons can
    /// play the UI click (<see cref="Click"/>) and the window open/close whoosh (<see cref="FrameRound"/>) with no
    /// running gameplay instance. A lazily-created DontDestroyOnLoad host carries the AudioSource so any screen can
    /// call <see cref="Play"/> from a static context.
    /// </summary>
    public sealed class UiSfx : MonoBehaviour
    {
        public const string Click = "SE_0001";        // every button press
        public const string FrameRound = "Frameround"; // dialog window open / close
        public const string Menufloat = "Menufloat";   // pointer slides onto a menu item / dropdown row (hover)
        public const string ButtonFloat = "Buttonfloat"; // pointer slides onto a room button (滑過)
        public const string WindowSlide = "Interfaceout"; // room UI collapse/expand slide (uihide/uidisplay 按下)
        public const string GameStart = "Start";       // 開始 pressed -> fade to the stage

        public const string Bubble = "Bubble";         // room speech bubble pop

        private static UiSfx _inst;
        private AudioSource _src;
        private readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();

        private static UiSfx Instance
        {
            get
            {
                if (_inst != null) return _inst;
                var go = new GameObject("UiSfx");
                DontDestroyOnLoad(go);
                // Audio needs a listener; the front-end usually has one (scene Main Camera). Only add one when truly
                // absent, to avoid Unity's "multiple audio listeners" warning during gameplay.
                if (FindAnyObjectByType<AudioListener>() == null) go.AddComponent<AudioListener>();
                _inst = go.AddComponent<UiSfx>();
                _inst._src = go.AddComponent<AudioSource>();
                _inst._src.playOnAwake = false;
                _inst._src.spatialBlend = 0f;
                return _inst;
            }
        }

        /// <summary>Play a one-shot SE by base name (no extension). No-op if the clip is missing.</summary>
        public static void Play(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            Instance.StartCoroutine(Instance.PlayCo(name));
        }

        /// <summary>Wire the standard button-press click (<see cref="Click"/>) onto a uGUI Button's onClick.</summary>
        public static void AttachClick(Button b)
        {
            if (b == null) return;
            b.onClick.AddListener(() => Play(Click));
        }

        /// <summary>Wire a one-shot SE (by base name) onto a Button's press. No-op if the button or name is missing.</summary>
        public static void AttachPress(Button b, string sound)
        {
            if (b == null || string.IsNullOrEmpty(sound)) return;
            b.onClick.AddListener(() => Play(sound));
        }

        private IEnumerator PlayCo(string name)
        {
            if (!_cache.TryGetValue(name, out var clip))
            {
                var path = Path.Combine(SdoExtracted.SeDir, name + ".wav");
                if (File.Exists(path))
                    using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                    {
                        yield return req.SendWebRequest();
                        if (req.result == UnityWebRequest.Result.Success) clip = DownloadHandlerAudioClip.GetContent(req);
                    }
                _cache[name] = clip;   // cache null too (missing file -> never re-hit disk)
            }
            if (clip != null && _src != null) _src.PlayOneShot(clip);
        }
    }
}
