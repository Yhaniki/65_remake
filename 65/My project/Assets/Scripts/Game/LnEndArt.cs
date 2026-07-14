namespace Sdo.Game
{
    /// <summary>
    /// Where a note skin's LONG-NOTE END burst (官方 .DGE 的 <c>Eft_LnEnd</c> / <c>Eft_Longnote_End</c> 槽) comes from.
    /// <para>
    /// Every <c>EFFECT\GAME_EFT_&lt;n&gt;.DGE</c> manifest carries exactly one long-note-end entry, and the offline data
    /// splits two ways: the self-contained skins point at their OWN folder (<c>eft_8\Eft_Longnote_End.an</c> →
    /// <c>EFT_0_0..EFT_0_5.PNG</c>), the rest share <c>PUBLICEFT\Eft_LnEnd.an</c> → <c>EFT_LNEND0..EFT_LNEND5.PNG</c>.
    /// Both sets are 6 × 128px frames. NB: the EFT_2 / EFT_5 folders DO ship an EFT_0_* set, but their .DGE routes them
    /// to PUBLICEFT anyway (leftovers from the older eft_1/3/4 skin layout) — the manifest wins.
    /// </para>
    /// Indexed by the same skin index as <c>ScreenGameplay.NoteTypeEftSuffix</c> (0..10 = the 2D skins).
    /// The 3D skin is not in this space: it terminates a hold with the real HIT_SUO 3DEFT instead.
    /// </summary>
    public static class LnEndArt
    {
        /// <summary>Frames per burst (both sets ship 6).</summary>
        public const int FrameCount = 6;

        /// <summary>The shared source every non-self-contained skin's .DGE points at.</summary>
        public const string SharedFolder = "PUBLICEFT";

        /// <summary>EFFECT subfolder per skin index, read off the GAME_EFT_&lt;suffix&gt;.DGE manifests.</summary>
        public static readonly string[] Folders =
        {
            SharedFolder,   // 0  skin EFT_2   -> PUBLICEFT\Eft_LnEnd.an
            SharedFolder,   // 1  skin EFT_5   -> PUBLICEFT\Eft_LnEnd.an
            "EFT_8",        // 2               -> eft_8\Eft_Longnote_End.an
            "EFT_9",        // 3               -> eft_9\Eft_Longnote_End.an
            "EFT_10",       // 4               -> eft_10\Eft_Longnote_End.an
            SharedFolder,   // 5  skin EFT_11  -> PUBLICEFT\Eft_LnEnd.an
            "EFT_7",        // 6  custom EFT_3 -> eft_7\Eft_Longnote_End.an
            SharedFolder,   // 7  skin EFT_12  -> PUBLICEFT\Eft_LnEnd.an
            SharedFolder,   // 8  skin EFT_13  -> PUBLICEFT\Eft_LnEnd.an
            SharedFolder,   // 9  skin EFT_14  -> PUBLICEFT\Eft_LnEnd.an
            "EFT_PET",      // 10              -> EFT_PET\Eft_Longnote_End.an
        };

        /// <summary>The skin's burst folder; out-of-range indices fall back to the shared one.</summary>
        public static string Folder(int noteType)
            => noteType >= 0 && noteType < Folders.Length ? Folders[noteType] : SharedFolder;

        /// <summary>Frame file name in <see cref="Folder"/>: the shared set is EFT_LNEND&lt;i&gt;, own folders EFT_0_&lt;i&gt;.</summary>
        public static string FrameFile(int noteType, int frame)
            => Folder(noteType) == SharedFolder ? "EFT_LNEND" + frame + ".PNG" : "EFT_0_" + frame + ".PNG";
    }
}
