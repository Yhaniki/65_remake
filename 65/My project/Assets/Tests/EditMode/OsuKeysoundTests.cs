using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Tests
{
    public class OsuKeysoundTests
    {
        private const string KeyedMap =
            "osu file format v14\n" +
            "[General]\n" +
            "AudioFilename: virtual\n" +
            "Mode: 3\n" +
            "[Metadata]\n" +
            "Title:Keysounded\n" +
            "Artist:Tester\n" +
            "BeatmapSetID:42\n" +
            "[Difficulty]\n" +
            "CircleSize:4\n" +
            "[Events]\n" +
            "Sample,1000,0,\"back,one.wav\",80\n" +
            "Sample,1000,0,\"back,one.wav\",80\n" +
            "5,1100,0,\"legacy.wav\",55\n" +
            "[TimingPoints]\n" +
            "0,500,4,2,0,35,1,0\n" +
            "1300,-100,4,2,0,65,0,0\n" +
            "[HitObjects]\n" +
            "64,192,1200,1,0,0:0:0:70:note.wav\n" +
            "192,192,1400,128,0,1600:0:0:0:0:hold.wav\n";

        [Test]
        public void Parser_Preserves_Storyboard_Multiplicity_And_Custom_Hit_Samples()
        {
            var map = OsuBeatmapParser.Parse(KeyedMap);

            Assert.AreEqual("virtual", map.AudioFilename);
            Assert.AreEqual(3, map.SampleEvents.Count);
            Assert.AreEqual(1000, map.SampleEvents[0].TimeMs);
            Assert.AreEqual("back,one.wav", map.SampleEvents[0].Filename);
            Assert.AreEqual("back,one.wav", map.SampleEvents[1].Filename,
                "identical simultaneous events are intentional voices and must not be deduped");
            Assert.AreEqual(80, map.SampleEvents[0].Volume);
            Assert.AreEqual("legacy.wav", map.SampleEvents[2].Filename);

            Assert.AreEqual("note.wav", map.HitObjects[0].CustomSampleFilename);
            Assert.AreEqual(70, map.HitObjects[0].CustomSampleVolume);
            Assert.AreEqual("hold.wav", map.HitObjects[1].CustomSampleFilename);
            Assert.AreEqual(0, map.HitObjects[1].CustomSampleVolume, "zero inherits the timing-point volume");
            Assert.AreEqual(35, map.SampleVolumeAt(1200));
            Assert.AreEqual(65, map.SampleVolumeAt(1400));
            Assert.IsFalse(map.SamplesMatchPlaybackRate, "the osu file-format default is false");
        }

        [Test]
        public void Parser_Reads_SamplesMatchPlaybackRate()
        {
            var map = OsuBeatmapParser.Parse(
                "osu file format v14\n[General]\nAudioFilename: virtual\nMode: 3\n" +
                "SamplesMatchPlaybackRate: 1\n[Difficulty]\nCircleSize:4\n");

            Assert.IsTrue(map.SamplesMatchPlaybackRate);
        }

        [Test]
        public void Metadata_Counts_Both_Automatic_And_Note_Keysounds()
        {
            var meta = OsuBeatmapParser.ReadMeta(KeyedMap);
            Assert.AreEqual(2, meta.NoteCount);
            Assert.AreEqual(5, meta.KeysoundCount);
        }

        [Test]
        public void Virtual_Is_Only_The_Reserved_Silent_Track_Name()
        {
            Assert.IsTrue(OsuBeatmapParser.IsVirtualAudioFilename("virtual"));
            Assert.IsTrue(OsuBeatmapParser.IsVirtualAudioFilename("\"VIRTUAL\""));
            Assert.IsFalse(OsuBeatmapParser.IsVirtualAudioFilename("virtual.mp3"));
            Assert.IsFalse(OsuBeatmapParser.IsVirtualAudioFilename(""));
        }

        [Test]
        public void LeadIn_And_ShortHold_Conversion_Preserve_Keysound_Data()
        {
            var map = new OsuBeatmap { Keys = 4 };
            map.TimingPoints.Add(new OsuTimingPoint(100, 500, 42));
            map.SampleEvents.Add(new OsuSampleEvent(200, 0, "event.wav", 75));
            map.HitObjects.Add(new OsuHitObject(0, 300, 330,
                customSampleFilename: "note.wav", customSampleVolume: 64));

            map.ApplyLeadIn(700);

            Assert.AreEqual(900, map.SampleEvents[0].TimeMs);
            Assert.AreEqual("event.wav", map.SampleEvents[0].Filename);
            Assert.AreEqual(75, map.SampleEvents[0].Volume);
            Assert.AreEqual(800, map.TimingPoints[0].TimeMs);
            Assert.AreEqual(42, map.TimingPoints[0].SampleVolume);
            Assert.AreEqual(1000, map.HitObjects[0].StartTimeMs);
            Assert.AreEqual("note.wav", map.HitObjects[0].CustomSampleFilename);
            Assert.AreEqual(64, map.HitObjects[0].CustomSampleVolume);

            Assert.AreEqual(1, map.CollapseShortHolds(100));
            Assert.IsFalse(map.HitObjects[0].IsHold);
            Assert.AreEqual("note.wav", map.HitObjects[0].CustomSampleFilename);
            Assert.AreEqual(64, map.HitObjects[0].CustomSampleVolume);
        }

        [Test]
        public void Virtual_Maps_Group_By_Set_Instead_Of_Global_Virtual_Audio()
        {
            var metas = new List<OsuMeta>
            {
                new OsuMeta { AudioFilename = "virtual", BeatmapSetId = 10, NoteCount = 300 },
                new OsuMeta { AudioFilename = "VIRTUAL", BeatmapSetId = 10, NoteCount = 100 },
                new OsuMeta { AudioFilename = "virtual", BeatmapSetId = 11, NoteCount = 200 },
            };

            var groups = ExternalSongGrouper.GroupOsu(metas, new List<string> { "a.osu", "b.osu", "c.osu" });

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("set:10", groups[0].Key);
            CollectionAssert.AreEqual(new[] { 0, 1 }, groups[0].Charts);
            Assert.AreEqual("set:11", groups[1].Key);
        }
    }

    public class OsuKeysoundBankTests
    {
        private string _root;
        private string _songDir;
        private string _chartPath;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_keysound_" + Path.GetRandomFileName());
            _songDir = Path.Combine(_root, "song");
            Directory.CreateDirectory(_songDir);
            _chartPath = Path.Combine(_songDir, "chart.osu");
            File.WriteAllText(_chartPath, "");
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Test]
        public void Fake_Wav_With_Ogg_Magic_Uses_The_Ogg_Decoder()
        {
            string sample = Path.Combine(_songDir, "sample.wav");
            var bytes = new byte[64];
            bytes[0] = (byte)'O'; bytes[1] = (byte)'g'; bytes[2] = (byte)'g'; bytes[3] = (byte)'S';
            File.WriteAllBytes(sample, bytes);

            Assert.AreEqual(AudioType.OGGVORBIS, OsuKeysoundBank.DecoderType(sample));
        }

        [Test]
        public void Sample_Path_Must_Stay_Inside_The_Beatmap_Folder()
        {
            string nested = Path.Combine(_songDir, "keys");
            Directory.CreateDirectory(nested);
            string valid = Path.Combine(nested, "hit.wav");
            File.WriteAllBytes(valid, new byte[] { 0 });
            string outside = Path.Combine(_root, "outside.wav");
            File.WriteAllBytes(outside, new byte[] { 0 });

            Assert.AreEqual(Path.GetFullPath(valid),
                OsuKeysoundBank.ResolveSamplePath(_chartPath, "keys\\hit.wav"));
            Assert.AreEqual("", OsuKeysoundBank.ResolveSamplePath(_chartPath, "../outside.wav"));
            Assert.AreEqual("", OsuKeysoundBank.ResolveSamplePath(_chartPath, outside));
        }

        [Test]
        public void Loading_Dedupes_Filenames_But_Not_Trigger_Rows()
        {
            var map = new OsuBeatmap();
            map.SampleEvents.Add(new OsuSampleEvent(100, 0, "dup.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(100, 0, "dup.wav"));
            map.HitObjects.Add(new OsuHitObject(0, 100, customSampleFilename: "DUP.WAV"));
            map.HitObjects.Add(new OsuHitObject(1, 200, customSampleFilename: "other.wav"));

            var names = OsuKeysoundBank.ReferencedFilenames(map);

            Assert.AreEqual(2, names.Count, "the bank loads one clip per filename");
            Assert.AreEqual(2, map.SampleEvents.Count, "the playback schedule keeps both duplicate triggers");
        }

        [Test]
        public void Mp3_Sample_Decodes_On_A_Worker_And_Becomes_A_Clip()
        {
            WriteFakeMp3("sample.mp3");
            var map = MapWithSamples("sample.mp3");
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int decoderThread = mainThread;
            var bank = new OsuKeysoundBank(path =>
            {
                decoderThread = Thread.CurrentThread.ManagedThreadId;
                return TestPcm();
            });

            try
            {
                Drain(bank.Load(map, _chartPath));

                Assert.AreNotEqual(mainThread, decoderThread,
                    "Mp3Decoder.Decode must stay off Unity's main thread");
                Assert.AreEqual(1, bank.LoadedCount);
                Assert.AreEqual(0, bank.MissingCount);
                Assert.IsTrue(bank.TryGet("sample.mp3", out var clip));
                Assert.AreEqual("osu:sample.mp3", clip.name);
            }
            finally
            {
                bank.Dispose();
            }
        }

        [Test]
        public void Mp3_Decode_Failure_Is_Counted_As_Missing()
        {
            WriteFakeMp3("broken.mp3");
            var bank = new OsuKeysoundBank(path => null);
            try
            {
                Drain(bank.Load(MapWithSamples("broken.mp3"), _chartPath));

                Assert.AreEqual(0, bank.LoadedCount);
                Assert.AreEqual(1, bank.MissingCount);
            }
            finally
            {
                bank.Dispose();
            }
        }

        [Test]
        public void Batch_Starts_All_Cheap_Mp3_Decodes_Before_First_Yield()
        {
            WriteFakeMp3("one.mp3");
            WriteFakeMp3("two.mp3");
            int started = 0;
            var gate = new ManualResetEventSlim(false);
            var bank = new OsuKeysoundBank(path =>
            {
                Interlocked.Increment(ref started);
                gate.Wait(5000);
                return TestPcm();
            });
            IEnumerator load = bank.Load(MapWithSamples("one.mp3", "two.mp3"), _chartPath);

            try
            {
                Assert.IsTrue(load.MoveNext(), "the prepared batch yields one frame");
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 2, 3000),
                    "both cheap decodes must start in the first batch, not one per frame");
                Assert.AreEqual(2, Volatile.Read(ref started));

                gate.Set();
                Drain(load);
                Assert.AreEqual(2, bank.LoadedCount);
                Assert.AreEqual(0, bank.MissingCount);
            }
            finally
            {
                gate.Set();
                (load as IDisposable)?.Dispose();
                bank.Dispose();
                gate.Dispose();
            }
        }

        [Test]
        public void Dispose_Stops_An_Old_Load_Before_Starting_Its_Next_Batch()
        {
            int count = OsuKeysoundBank.LoadBatchSize + 1;
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = "sample" + i + ".mp3";
                WriteFakeMp3(names[i]);
            }

            int started = 0;
            var bank = new OsuKeysoundBank(path =>
            {
                Interlocked.Increment(ref started);
                return TestPcm();
            });
            IEnumerator load = bank.Load(MapWithSamples(names), _chartPath);

            try
            {
                Assert.IsTrue(load.MoveNext());
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => Volatile.Read(ref started) == OsuKeysoundBank.LoadBatchSize, 3000));

                bank.Dispose();

                Assert.IsFalse(load.MoveNext(),
                    "generation change must end the old iterator without waiting for its worker");
                Assert.AreEqual(OsuKeysoundBank.LoadBatchSize, Volatile.Read(ref started),
                    "disposing must prevent the next batch from starting");
            }
            finally
            {
                (load as IDisposable)?.Dispose();
                bank.Dispose();
            }
        }

        private void WriteFakeMp3(string name)
        {
            File.WriteAllBytes(Path.Combine(_songDir, name),
                new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0, 0, 0 });
        }

        private static OsuBeatmap MapWithSamples(params string[] names)
        {
            var map = new OsuBeatmap();
            for (int i = 0; i < names.Length; i++)
                map.SampleEvents.Add(new OsuSampleEvent(i * 100, 0, names[i]));
            return map;
        }

        private static Mp3Pcm TestPcm()
        {
            return new Mp3Pcm
            {
                Samples = new[] { 0.25f, -0.25f, 0.5f, -0.5f },
                Channels = 2,
                SampleRate = 44100
            };
        }

        private static void Drain(IEnumerator routine)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (routine.MoveNext())
            {
                if (watch.ElapsedMilliseconds > 5000)
                    Assert.Fail("keysound load did not finish within five seconds");
                Thread.Sleep(1);
            }
        }
    }

    public class OsuKeysoundPreviewTimelineTests
    {
        [Test]
        public void Preview_Queues_Events_And_All_Playable_Note_Heads_Without_Deduplication()
        {
            var map = new OsuBeatmap();
            map.TimingPoints.Add(new OsuTimingPoint(0, 500, 37));
            map.SampleEvents.Add(new OsuSampleEvent(99, 0, "before.wav", 10));
            map.SampleEvents.Add(new OsuSampleEvent(100, 0, "dup.wav", 20));
            map.SampleEvents.Add(new OsuSampleEvent(100, 0, "dup.wav", 20));
            map.SampleEvents.Add(new OsuSampleEvent(300, 0, "at-end.wav", 30));
            map.HitObjects.Add(new OsuHitObject(
                0, 100, customSampleFilename: "tap.wav"));
            map.HitObjects.Add(new OsuHitObject(
                1, 100, 200, customSampleFilename: "hold.wav", customSampleVolume: 80));
            map.HitObjects.Add(new OsuHitObject(
                2, 100, isBomb: true, customSampleFilename: "bomb.wav"));
            map.HitObjects.Add(new OsuHitObject(
                2, 100, isFake: true, customSampleFilename: "fake.wav"));
            map.HitObjects.Add(new OsuHitObject(
                3, 299, customSampleFilename: "last.wav", customSampleVolume: 60));
            map.HitObjects.Add(new OsuHitObject(
                3, 300, customSampleFilename: "note-at-end.wav"));

            var triggers = OsuKeysoundPreviewPlayer.BuildTriggers(map, 100, 300);

            Assert.AreEqual(5, triggers.Count, "the preview window is [start,end)");
            int duplicateRows = 0;
            int inheritedVolume = -1;
            int holdVolume = -1;
            foreach (var trigger in triggers)
            {
                if (trigger.Filename == "dup.wav") duplicateRows++;
                if (trigger.Filename == "tap.wav") inheritedVolume = trigger.Volume;
                if (trigger.Filename == "hold.wav") holdVolume = trigger.Volume;
                Assert.AreNotEqual("bomb.wav", trigger.Filename);
                Assert.AreNotEqual("fake.wav", trigger.Filename);
            }
            Assert.AreEqual(2, duplicateRows, "identical rows are independent voices");
            Assert.AreEqual(37, inheritedVolume, "zero note volume inherits the timing point");
            Assert.AreEqual(80, holdVolume, "a hold plays its custom sample at the head");
        }

        [Test]
        public void Preview_Window_Loads_Only_Triggers_Inside_The_Active_Twenty_Seconds()
        {
            var map = new OsuBeatmap();
            map.SampleEvents.Add(new OsuSampleEvent(0, 0, "a.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(1, 0, "a.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(19999, 0, "b.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(20000, 0, "at-end.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(50000, 0, "later.wav"));

            OsuKeysoundPreviewPlayer.ResolveWindow(
                map, 0, OsuKeysoundPreviewPlayer.DefaultWindowMs,
                out double startMs, out double endMs);
            var triggers = OsuKeysoundPreviewPlayer.BuildTriggers(map, startMs, endMs);

            Assert.AreEqual(0.0, startMs);
            Assert.AreEqual(20000.0, endMs);
            Assert.AreEqual(3, triggers.Count);
            Assert.AreEqual("b.wav", triggers[2].Filename);
        }

        [Test]
        public void Preview_Window_Does_Not_Collapse_Around_A_Short_Map_And_Repeat_Long_Tails()
        {
            var map = new OsuBeatmap();
            map.SampleEvents.Add(new OsuSampleEvent(1000, 0, "long-tail.wav"));

            OsuKeysoundPreviewPlayer.ResolveWindow(
                map, 0, OsuKeysoundPreviewPlayer.DefaultWindowMs,
                out double startMs, out double endMs);
            var triggers = OsuKeysoundPreviewPlayer.BuildTriggers(map, startMs, endMs);

            Assert.AreEqual(0.0, startMs);
            Assert.AreEqual(20000.0, endMs,
                "a short onset timeline still uses the requested preview period; " +
                "otherwise a sample longer than the fixed tail overlaps itself every loop");
            Assert.AreEqual(1, triggers.Count);
            Assert.AreEqual("long-tail.wav", triggers[0].Filename);
        }

        [Test]
        public void Preview_Loop_Keeps_One_Dsp_Anchor_Without_A_Restart_Gap()
        {
            const double anchorDsp = 100.0;
            const double startMs = 1000.0;
            const double endMs = 21000.0;

            Assert.That(
                OsuKeysoundPreviewPlayer.OccurrenceDspTime(
                    anchorDsp, startMs, endMs, startMs, 0),
                Is.EqualTo(100.0).Within(1e-9));
            Assert.That(
                OsuKeysoundPreviewPlayer.OccurrenceDspTime(
                    anchorDsp, startMs, endMs, 20999.0, 0),
                Is.EqualTo(119.999).Within(1e-9));
            Assert.That(
                OsuKeysoundPreviewPlayer.OccurrenceDspTime(
                    anchorDsp, startMs, endMs, startMs, 1),
                Is.EqualTo(120.0).Within(1e-9),
                "the next loop begins on the same anchor without another 50ms lead");

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                OsuKeysoundPreviewPlayer.OccurrenceDspTime(0, 10, 10, 10, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                OsuKeysoundPreviewPlayer.OccurrenceDspTime(0, 0, 1000, 0, -1));
        }
    }

    public class OsuKeysoundSchedulerStateTests
    {
        private const System.Reflection.BindingFlags InstancePrivate =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        private GameObject _go;
        private ScreenGameplay _screen;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("osu-keysound-scheduler-test");
            _screen = _go.AddComponent<ScreenGameplay>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
        }

        [Test]
        public void Auto_Eligibility_Uses_Map_Index_For_Duplicate_Notes_And_Skips_Judged_State()
        {
            var map = new OsuBeatmap();
            var duplicate = new OsuHitObject(
                lane: 0, startTimeMs: 100, customSampleFilename: "hit.wav");
            map.HitObjects.Add(duplicate);
            map.HitObjects.Add(duplicate);
            Field("_map").SetValue(_screen, map);

            Type runtimeType = typeof(ScreenGameplay).GetNestedType(
                "RuntimeNote", System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(runtimeType);
            var constructor = runtimeType.GetConstructor(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(OsuHitObject), typeof(int) },
                modifiers: null);
            Assert.NotNull(constructor);
            object first = constructor.Invoke(new object[] { duplicate, 0 });
            object second = constructor.Invoke(new object[] { duplicate, 0 });
            runtimeType.GetField("HeadJudged").SetValue(first, true);

            var byMapIndex = (IList)Field("_notesByMapIndex").GetValue(_screen);
            byMapIndex.Add(first);
            byMapIndex.Add(second);
            var eligible = typeof(ScreenGameplay).GetMethod(
                "IsOsuAutoNoteEligible", InstancePrivate);
            Assert.NotNull(eligible);

            Assert.IsFalse((bool)eligible.Invoke(_screen, new object[] { 0 }),
                "the judged first duplicate must not be late-scheduled");
            Assert.IsTrue((bool)eligible.Invoke(_screen, new object[] { 1 }),
                "an identical but still-unjudged second row remains eligible");

            runtimeType.GetField("HeadJudged").SetValue(first, false);
            var scheduled = (HashSet<int>)Field("_osuScheduledAutoNotes").GetValue(_screen);
            scheduled.Add(0);
            Assert.IsFalse((bool)eligible.Invoke(_screen, new object[] { 0 }),
                "a DSP-scheduled note must not be queued twice");
            scheduled.Clear();
            runtimeType.GetField("Done").SetValue(first, true);
            Assert.IsFalse((bool)eligible.Invoke(_screen, new object[] { 0 }),
                "a retired note must never be replayed when ShowTime turns on");
        }

        [Test]
        public void Direct_Seek_Skips_Exact_OneShot_But_Resume_Preserves_Consumed_Cursor()
        {
            var map = new OsuBeatmap();
            map.SampleEvents.Add(new OsuSampleEvent(100, 0, "a.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(200, 0, "b.wav"));
            map.SampleEvents.Add(new OsuSampleEvent(300, 0, "c.wav"));
            Field("_map").SetValue(_screen, map);

            Invoke("OnOsuPlaybackSeek", 200.0);
            Assert.AreEqual(2, Field("_osuEventIndex").GetValue(_screen),
                "seek does not backfill the one-shot exactly at its destination");

            Field("_osuEventIndex").SetValue(_screen, 3);
            Invoke("OnOsuPlaybackResumed", 0.0, AudioSettings.dspTime + 0.05);
            Assert.AreEqual(3, Field("_osuEventIndex").GetValue(_screen),
                "resume must not rewind the true DSP-consumed storyboard cursor");
        }

        [Test]
        public void Editor_End_Includes_Recalculated_Keysound_Tail()
        {
            Field("_totalMs").SetValue(_screen, 1000.0);
            Field("_osuTimelineEndMs").SetValue(_screen, 5000.0);

            Invoke("RefreshEditorTimelineEnd");

            Assert.AreEqual(5000.0, _screen.EditorEndMs);
        }

        [Test]
        public void Editor_Preview_AutoSchedules_Note_Keysounds_Except_In_Beat_Test()
        {
            var property = typeof(ScreenGameplay).GetProperty(
                "ShouldScheduleOsuAutoNotes", InstancePrivate);
            Assert.NotNull(property);

            _screen.editorMode = true;
            _screen.beatTestMode = false;
            Assert.IsTrue((bool)property.GetValue(_screen),
                "normal chart-editor playback should sound every note sample");

            _screen.beatTestMode = true;
            Assert.IsFalse((bool)property.GetValue(_screen),
                "the generated beat-test chart has no osu keysound preview");
        }

        [Test]
        public void Editor_EarlyMiss_DoesNotCancel_Future_AutoPreviewSample()
        {
            var map = new OsuBeatmap();
            var note = new OsuHitObject(
                lane: 0, startTimeMs: 1000, customSampleFilename: "hit.wav");
            map.HitObjects.Add(note);
            Field("_map").SetValue(_screen, map);
            Invoke("AddOsuEventVoice");

            var starts = (List<double>)Field("_osuEventStartsAt").GetValue(_screen);
            var busyUntil = (List<double>)Field("_osuEventBusyUntil").GetValue(_screen);
            var owners = (List<int>)Field("_osuAutoNoteOwners").GetValue(_screen);
            var scheduled = (HashSet<int>)Field("_osuScheduledAutoNotes").GetValue(_screen);
            double futureDsp = AudioSettings.dspTime + 1.0;
            starts[0] = futureDsp;
            busyUntil[0] = futureDsp + 1.0;
            owners[0] = 0;
            scheduled.Add(0);

            _screen.editorMode = true;
            Invoke("PlayOsuHitSample", note, Sdo.Ruleset.Judgment.Miss);

            Assert.AreEqual(futureDsp, starts[0], 1e-6,
                "editor misses only detach ownership; the preview sound remains scheduled");
            Assert.AreEqual(-1, owners[0]);

            starts[0] = futureDsp;
            busyUntil[0] = futureDsp + 1.0;
            owners[0] = 0;
            scheduled.Add(0);
            _screen.editorMode = false;
            Invoke("PlayOsuHitSample", note, Sdo.Ruleset.Judgment.Miss);

            Assert.AreEqual(0.0, starts[0],
                "gameplay misses still cancel future auto-scheduled samples");
            Assert.AreEqual(-1, owners[0]);
        }

        private static System.Reflection.FieldInfo Field(string name)
        {
            var field = typeof(ScreenGameplay).GetField(name, InstancePrivate);
            Assert.NotNull(field, name);
            return field;
        }

        private void Invoke(string name, params object[] arguments)
        {
            var method = typeof(ScreenGameplay).GetMethod(name, InstancePrivate);
            Assert.NotNull(method, name);
            method.Invoke(_screen, arguments);
        }
    }
    public class VirtualOsuScannerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdo_virtual_scan_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Test]
        public void Keysounded_Virtual_Map_Is_Kept_Without_Borrowing_A_Sample_As_Music()
        {
            WriteOsu("virtual", includeKeysounds: true);
            File.WriteAllBytes(Path.Combine(_root, "event.wav"), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(_root, "note.wav"), new byte[] { 0 });

            var songs = ExternalSongScanner.LoadFolder("pack", _root);

            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("", songs[0].AudioPath);
            Assert.IsTrue(HasChart(songs[0]), "the virtual song remains playable through its chart");
        }

        [Test]
        public void Broken_NonVirtual_Map_Does_Not_Guess_When_Several_Audio_Files_Exist()
        {
            WriteOsu("missing.mp3", includeKeysounds: false);
            File.WriteAllBytes(Path.Combine(_root, "one.wav"), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(_root, "two.wav"), new byte[] { 0 });

            Assert.AreEqual(0, ExternalSongScanner.LoadFolder("pack", _root).Count);
        }

        [Test]
        public void Virtual_Sentinel_Is_Kept_Even_When_Keysounds_Are_Absent()
        {
            WriteOsu("virtual", includeKeysounds: false);
            File.WriteAllBytes(Path.Combine(_root, "unrelated.wav"), new byte[] { 0 });

            var songs = ExternalSongScanner.LoadFolder("pack", _root);

            Assert.AreEqual(1, songs.Count);
            Assert.AreEqual("", songs[0].AudioPath);
        }

        private void WriteOsu(string audioFilename, bool includeKeysounds)
        {
            string events = includeKeysounds ? "Sample,500,0,\"event.wav\",100\n" : "";
            string hitSample = includeKeysounds ? "0:0:0:100:note.wav" : "0:0:0:0:";
            string text =
                "osu file format v14\n" +
                "[General]\nAudioFilename: " + audioFilename + "\nMode: 3\n" +
                "[Metadata]\nTitle:Virtual Test\nArtist:Tester\nVersion:Hard\nBeatmapSetID:123\n" +
                "[Difficulty]\nCircleSize:4\n" +
                "[Events]\n" + events +
                "[TimingPoints]\n0,500,4,2,0,100,1,0\n" +
                "[HitObjects]\n64,192,500,1,0," + hitSample + "\n";
            File.WriteAllText(Path.Combine(_root, "chart.osu"), text);
        }

        private static bool HasChart(ExternalSong song)
        {
            foreach (var chart in song.Charts)
                if (chart != null) return true;
            return false;
        }
    }
}
