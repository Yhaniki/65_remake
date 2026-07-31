using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 編輯器的 mp3 解碼快取。整包 .mp3 的歌一首要解 ~1.1 秒（NLayer 整首解），一首一首校 offset 時
    /// 這一秒全卡在 Q/E 上 —— 快取＋預抓就是為了把它變成 0。這裡測的是「留誰、丟誰」那層邏輯。
    /// </summary>
    public class EditorAudioCacheTests
    {
        private static string P(string name) => Path.Combine(Path.GetTempPath(), name);

        [SetUp]
        public void SetUp() => EditorAudioCache.Clear();

        [TearDown]
        public void TearDown() => EditorAudioCache.Clear();

        [Test]
        public void SamePathIsDecodedOnlyOnce()
        {
            var a = EditorAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            var b = EditorAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            Assert.AreSame(a, b, "第二次要拿到同一個工作，而不是再解一次");
            Assert.AreEqual(1, EditorAudioCache.Count);
        }

        [Test]
        public void OldestIsEvictedOnceCapacityIsReached()
        {
            // PCM 一首就幾十 MB，所以只留「現在這首＋前後各一首」。
            for (int i = 0; i < EditorAudioCache.Capacity + 2; i++)
                EditorAudioCache.Get(P($"song{i}.mp3"), Mp3Decoder.Mp3Sync.Osu);
            Assert.AreEqual(EditorAudioCache.Capacity, EditorAudioCache.Count);
        }

        [Test]
        public void UsingAnEntryKeepsItFromBeingEvicted()
        {
            // 校時常見動作：E 到下一首、Q 回來再聽。回來的那首必須還在快取裡。
            EditorAudioCache.Get(P("keep.mp3"), Mp3Decoder.Mp3Sync.Osu);
            for (int i = 0; i < EditorAudioCache.Capacity - 1; i++)
                EditorAudioCache.Get(P($"filler{i}.mp3"), Mp3Decoder.Mp3Sync.Osu);
            var again = EditorAudioCache.Get(P("keep.mp3"), Mp3Decoder.Mp3Sync.Osu);   // 用一次 → 變成最新
            EditorAudioCache.Get(P("newest.mp3"), Mp3Decoder.Mp3Sync.Osu);             // 擠掉的應該是 filler0

            Assert.AreSame(again, EditorAudioCache.Get(P("keep.mp3"), Mp3Decoder.Mp3Sync.Osu));
            Assert.AreEqual(EditorAudioCache.Capacity, EditorAudioCache.Count);
        }

        [Test]
        public void PrefetchOnlyBothersWithMp3()
        {
            // ogg/wav 由 Unity 原生解，本來就快 —— 預抓只是白占幾十 MB。
            EditorAudioCache.Prefetch(P("song.ogg"), Mp3Decoder.Mp3Sync.Osu);
            EditorAudioCache.Prefetch(P("song.wav"), Mp3Decoder.Mp3Sync.Osu);
            EditorAudioCache.Prefetch("", Mp3Decoder.Mp3Sync.Osu);
            Assert.AreEqual(0, EditorAudioCache.Count);

            EditorAudioCache.Prefetch(P("song.mp3"), Mp3Decoder.Mp3Sync.Osu);
            Assert.AreEqual(1, EditorAudioCache.Count);
        }

        [Test]
        public void ClearReleasesEverything()
        {
            EditorAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            EditorAudioCache.Clear();
            Assert.AreEqual(0, EditorAudioCache.Count);
        }

        [Test]
        public void SyncIsPickedFromTheChartsHomeFormat()
        {
            // .gn 歌曲包(3) 跟 osu(1) 都走 Osu：它們的 mp3 是從原始音檔轉出來的，要把編碼器 priming 修掉。
            Assert.AreEqual(Mp3Decoder.Mp3Sync.Osu, ScreenGameplay.Mp3SyncFor(1));
            Assert.AreEqual(Mp3Decoder.Mp3Sync.Osu, ScreenGameplay.Mp3SyncFor(3));
            Assert.AreEqual(Mp3Decoder.Mp3Sync.StepMania, ScreenGameplay.Mp3SyncFor(2));
            Assert.AreEqual(Mp3Decoder.Mp3Sync.StepMania, ScreenGameplay.Mp3SyncFor(0));
        }
    }
}
