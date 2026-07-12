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
    }

    /// <summary>
    /// A user song discovered under Songs/&lt;group&gt;/&lt;folder&gt;/ (or an AdditionalSongFolders root). Carries the
    /// resolved audio + cover image paths and up to three 4K difficulty slots (index 0=easy, 1=normal, 2=hard),
    /// filled hard-first from the highest-note-count charts (see <see cref="ExternalDifficultyPicker"/>).
    /// </summary>
    public sealed class ExternalSong
    {
        public string Group = "";
        public string FolderPath = "";
        public string Title = "";
        public string Artist = "";
        public double Bpm;
        public string AudioPath = "";   // absolute; "" if no audio file found
        public string ImagePath = "";   // absolute cover (jacket→banner→background); "" if none
        public SongFormat Format;

        // Preview clip window (from osu PreviewTime / StepMania #SAMPLESTART+#SAMPLELENGTH). Song-select loops this
        // window of the full audio instead of a middle-of-song default.
        public int PreviewStartMs = -1;   // -1 = unspecified → fall back to a centred default window
        public int PreviewLengthMs;       // 0 = unspecified → use the default preview length

        /// <summary>[easy, normal, hard]; a slot is null when there aren't enough charts to fill it.</summary>
        public readonly ExternalChart[] Charts = new ExternalChart[3];

        public bool Playable => Charts[0] != null || Charts[1] != null || Charts[2] != null;
    }
}
