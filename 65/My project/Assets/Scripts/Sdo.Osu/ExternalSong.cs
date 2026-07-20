namespace Sdo.Osu
{
    /// <summary>On-disk chart format of an external (user-dropped) song folder.</summary>
    public enum SongFormat { None = 0, Osu = 1, Sm = 2 }

    /// <summary>One playable difficulty of an external song (one .osu file, or one #NOTES block in a .sm).</summary>
    public sealed class ExternalChart
    {
        public string FilePath = "";   // absolute path to the .osu / .sm file
        public int ChartIndex;         // .sm: index of the #NOTES block; .osu: 0
        public int NoteCount;          // objects (taps + holds) — used to rank difficulties
        public int Level;              // .sm meter / .osu unknown(0) — shown as the LV label
        public int DurationSec;        // last note's time — the 時間 column, same measure as the official catalog's dur*
    }

    /// <summary>
    /// A user song discovered under Songs/&lt;group&gt;/&lt;folder&gt;/ (or an AdditionalSongFolders root). Carries the
    /// resolved audio + cover image paths and up to three 4K difficulty slots (index 0=easy, 1=normal, 2=hard),
    /// filled hard-first from the highest-note-count charts (see <see cref="ExternalDifficultyPicker"/>).
    ///
    /// One folder may hold SEVERAL songs (several beatmap sets flattened together, or several .sm files); each gets
    /// its own record, keyed by <see cref="SongKey"/> (see <see cref="ExternalSongGrouper"/>).
    /// </summary>
    public sealed class ExternalSong
    {
        public string Group = "";
        public string FolderPath = "";

        /// <summary>Identity of this song WITHIN its folder — the grouping key (audio file, else set id / metadata /
        /// chart filename). "" when the folder holds a single song, which keeps that song's catalog gn (a hash of
        /// FolderPath + SongKey) byte-identical to the one-song-per-folder era, so existing favourites survive.</summary>
        public string SongKey = "";

        public string Title = "";
        public string Artist = "";
        public double Bpm;
        public string AudioPath = "";   // absolute; "" if no audio file found
        public int AudioDurationSec;    // the music file's own play time (see AudioDuration). Left 0 by the scanner
                                        // (reading it means decoding the audio — deferred to song-select); 0 → the 時間
                                        // column falls back to the chart's last-note time until then.
        public string ImagePath = "";   // absolute cover (jacket→banner→background); "" if none
        public SongFormat Format;

        // ---- from the folder's sdo.header sidecar (see SongSidecar) ----

        /// <summary>Absolute path of the generated CD disc image; "" = the sidecar records none (or its file is gone),
        /// i.e. the disc still has to be composed from <see cref="ImagePath"/> the first time this song is selected.</summary>
        public string CdImagePath = "";

        /// <summary>Reserved: the dance / camera files the sidecar points at ("" = none). Read today, unused by
        /// gameplay — the format is in place for when user .mot / camera files land in song folders.</summary>
        public string MotPath = "";
        public string CameraPath = "";

        /// <summary>Per-song timing offset (ms) recorded in the folder's sidecar by the chart editor (F11/F12 → Ctrl+S);
        /// positive delays the music. Flows to <see cref="SongCatalog.Entry.offsetMs"/> so it also applies in gameplay.</summary>
        public float OffsetMs;

        // Preview clip window (from osu PreviewTime / StepMania #SAMPLESTART+#SAMPLELENGTH). Song-select loops this
        // window of the full audio instead of a middle-of-song default.
        public int PreviewStartMs = -1;   // -1 = unspecified → fall back to a centred default window
        public int PreviewLengthMs;       // 0 = unspecified → use the default preview length

        /// <summary>[easy, normal, hard]; a slot is null when there aren't enough charts to fill it.</summary>
        public readonly ExternalChart[] Charts = new ExternalChart[3];

        public bool Playable => Charts[0] != null || Charts[1] != null || Charts[2] != null;
    }
}
