using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 正式遊玩的 mp3 解碼快取(GameplaySongAudioCache)。外部歌音檔常是 mp3,桌面版 Unity 不解,得用 NLayer
    /// 整首解 ~1.4 秒;選歌確認(OnConfirm)時預抓、進遊戲命中就秒進。這裡測「留誰、丟誰」和「key 含 sync
    /// 不會串位置」那層純邏輯(不真的解碼 —— 暫存路徑不存在,Get 只是登記一個工作)。
    /// </summary>
    public class GameplaySongAudioCacheTests
    {
        private static string P(string name) => Path.Combine(Path.GetTempPath(), name);

        [SetUp]
        public void SetUp() => GameplaySongAudioCache.Clear();

        [TearDown]
        public void TearDown() => GameplaySongAudioCache.Clear();

        [Test]
        public void SamePathAndSyncIsDecodedOnlyOnce()
        {
            var a = GameplaySongAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            var b = GameplaySongAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            Assert.AreSame(a, b, "第二次要拿到同一個工作,而不是再解一次");
            Assert.AreEqual(1, GameplaySongAudioCache.Count);
        }

        [Test]
        public void SamePathDifferentSyncKeptApart()
        {
            // 同一個 mp3 用 osu / StepMania 解出來的位置不同(見 Mp3Decoder.Mp3Sync),要分開存,不能互相頂掉。
            var osu = GameplaySongAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            var sm = GameplaySongAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.StepMania);
            Assert.AreNotSame(osu, sm);
            Assert.AreEqual(2, GameplaySongAudioCache.Count);
        }

        [Test]
        public void KeyIncludesSync()
        {
            Assert.AreNotEqual(
                GameplaySongAudioCache.Key(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu),
                GameplaySongAudioCache.Key(P("a.mp3"), Mp3Decoder.Mp3Sync.StepMania));
        }

        [Test]
        public void OldestIsEvictedOnceCapacityIsReached()
        {
            // PCM 一首幾十 MB,只留 Capacity 首,滿了丟最舊的。
            for (int i = 0; i < GameplaySongAudioCache.Capacity + 2; i++)
                GameplaySongAudioCache.Get(P($"song{i}.mp3"), Mp3Decoder.Mp3Sync.Osu);
            Assert.AreEqual(GameplaySongAudioCache.Capacity, GameplaySongAudioCache.Count);
        }

        [Test]
        public void UsingAnEntryKeepsItFromBeingEvicted()
        {
            // retry / 選了又反悔選回來:回頭那首必須還在快取裡。
            GameplaySongAudioCache.Get(P("keep.mp3"), Mp3Decoder.Mp3Sync.Osu);
            for (int i = 0; i < GameplaySongAudioCache.Capacity - 1; i++)
                GameplaySongAudioCache.Get(P($"filler{i}.mp3"), Mp3Decoder.Mp3Sync.Osu);
            var again = GameplaySongAudioCache.Get(P("keep.mp3"), Mp3Decoder.Mp3Sync.Osu);   // 用一次 → 變最新
            GameplaySongAudioCache.Get(P("newest.mp3"), Mp3Decoder.Mp3Sync.Osu);             // 擠掉的應該是 filler0

            Assert.AreSame(again, GameplaySongAudioCache.Get(P("keep.mp3"), Mp3Decoder.Mp3Sync.Osu));
            Assert.AreEqual(GameplaySongAudioCache.Capacity, GameplaySongAudioCache.Count);
        }

        [Test]
        public void PrefetchOnlyBothersWithMp3()
        {
            // ogg/wav 由 Unity 原生解,本來就快 —— 預抓只是白占幾十 MB。
            GameplaySongAudioCache.Prefetch(P("song.ogg"), Mp3Decoder.Mp3Sync.Osu);
            GameplaySongAudioCache.Prefetch(P("song.wav"), Mp3Decoder.Mp3Sync.Osu);
            GameplaySongAudioCache.Prefetch("", Mp3Decoder.Mp3Sync.Osu);
            Assert.AreEqual(0, GameplaySongAudioCache.Count);

            GameplaySongAudioCache.Prefetch(P("song.mp3"), Mp3Decoder.Mp3Sync.Osu);
            Assert.AreEqual(1, GameplaySongAudioCache.Count);
        }

        [Test]
        public void ClearReleasesEverything()
        {
            GameplaySongAudioCache.Get(P("a.mp3"), Mp3Decoder.Mp3Sync.Osu);
            GameplaySongAudioCache.Clear();
            Assert.AreEqual(0, GameplaySongAudioCache.Count);
        }
    }
}
