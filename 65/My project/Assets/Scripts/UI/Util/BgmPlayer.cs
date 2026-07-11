using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Front-end background-music player for the lobby-style screens (男/女選擇 + ROOM). Plays the *.ogg / *.mp3 files in
    /// <see cref="SdoExtracted.UiBgmDir"/> (Extracted/UI/BGM) as an endless RANDOM playlist — when a track finishes the
    /// next one is picked at random, but NEVER the same track twice in a row. A lazily-created DontDestroyOnLoad host
    /// carries the AudioSource so any screen can drive it from a static context (mirrors <see cref="UiSfx"/>).
    ///
    /// Control is centralised in <c>FrontendApp</c>'s screen-change handler: <see cref="Play"/> on GenderSel/Room,
    /// <see cref="Stop"/> on every other screen (SongSelect has its own song previews, Gameplay plays the chart song).
    /// <see cref="Play"/> is idempotent — re-calling it while already playing keeps the current track going (so the
    /// GenderSel → Room transition does NOT restart the music); only <see cref="Stop"/> ends it.
    /// </summary>
    public sealed class BgmPlayer : MonoBehaviour
    {
        private static BgmPlayer _inst;
        private AudioSource _src;
        private List<string> _tracks;
        private int _last = -1;          // index of the track playing / just played → never repeated next
        private bool _playing;
        private Coroutine _loop;

        private static BgmPlayer Instance
        {
            get
            {
                if (_inst != null) return _inst;
                var go = new GameObject("BgmPlayer");
                DontDestroyOnLoad(go);
                // Audio needs a listener; only add one when truly absent (avoids Unity's "multiple audio listeners" warning).
                if (FindAnyObjectByType<AudioListener>() == null) go.AddComponent<AudioListener>();
                _inst = go.AddComponent<BgmPlayer>();
                _inst._src = go.AddComponent<AudioSource>();
                _inst._src.playOnAwake = false;
                _inst._src.loop = false;
                _inst._src.spatialBlend = 0f;
                _inst._src.volume = AudioMix.Bgm;
                AudioMix.Changed += _inst.OnMixChanged;   // 拖「背景音樂」滑桿時即時改 BGM 音量
                return _inst;
            }
        }

        /// <summary>Start the random BGM loop. Idempotent: does nothing if already playing (keeps the current track).</summary>
        public static void Play()
        {
            var inst = Instance;
            inst._src.volume = AudioMix.Bgm;   // 套用最新音量(可能停播期間被調過)
            if (inst._playing) return;
            inst._playing = true;
            inst._loop = inst.StartCoroutine(inst.PlayLoop());
        }

        /// <summary>Stop the BGM and release the current clip. Safe to call when nothing is playing.</summary>
        public static void Stop()
        {
            if (_inst == null) return;
            _inst._playing = false;
            if (_inst._loop != null) { _inst.StopCoroutine(_inst._loop); _inst._loop = null; }
            if (_inst._src != null)
            {
                _inst._src.Stop();
                var c = _inst._src.clip;
                _inst._src.clip = null;
                if (c != null) Destroy(c);
            }
        }

        private void OnMixChanged() { if (_src != null) _src.volume = AudioMix.Bgm; }

        private IEnumerator PlayLoop()
        {
            if (_tracks == null) _tracks = ScanTracks();
            if (_tracks.Count == 0) { Debug.LogWarning("[bgm] no *.ogg / *.mp3 in " + SdoExtracted.UiBgmDir); _playing = false; yield break; }

            while (_playing)
            {
                int i = PickNext();
                _last = i;
                AudioClip clip = null;
                yield return LoadClip(_tracks[i], c => clip = c);
                if (!_playing) yield break;
                if (clip == null) { yield return null; continue; }   // load failed → try another next round

                _src.clip = clip;
                _src.Play();
                while (_playing && _src.isPlaying) yield return null;
                if (_src.clip == clip) _src.clip = null;
                Destroy(clip);   // streamed BGM clips are large → free before the next track
            }
        }

        /// <summary>Pick the next track index at random, never the one just played (<see cref="_last"/>).</summary>
        private int PickNext()
        {
            int n = _tracks.Count;
            if (n <= 1) return 0;
            if (_last < 0) return Random.Range(0, n);      // first pick: any track
            int i = Random.Range(0, n - 1);                // pick among the OTHER n-1 tracks, then skip over _last
            if (i >= _last) i++;
            return i;
        }

        private static List<string> ScanTracks()
        {
            var list = new List<string>();
            try
            {
                var dir = SdoExtracted.UiBgmDir;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".ogg" || ext == ".mp3") list.Add(f);
                    }
            }
            catch { }
            list.Sort(System.StringComparer.OrdinalIgnoreCase);   // stable order so PickNext's index is meaningful
            return list;
        }

        private IEnumerator LoadClip(string path, System.Action<AudioClip> done)
        {
            var type = path.EndsWith(".mp3", System.StringComparison.OrdinalIgnoreCase) ? AudioType.MPEG : AudioType.OGGVORBIS;
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip dh) dh.streamAudio = true;   // stream (BGM is 1–2 MB each)
                yield return req.SendWebRequest();
                done(req.result == UnityWebRequest.Result.Success ? DownloadHandlerAudioClip.GetContent(req) : null);
            }
        }
    }
}
