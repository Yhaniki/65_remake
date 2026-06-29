namespace Sdo.Game
{
    /// <summary>
    /// ScnRoom (id 37) waiting-light marquee pattern — verbatim from sdo_stand_alone.exe.
    ///
    /// The open-room scene mounts eight "DENGDAI" lights (GUANG1..GUANG8). They are NOT independently animated: their
    /// glow texture is swapped on a shared 24-row × 8-column on/off table at <c>DAT_00552230</c>. Every 150 ms the row
    /// index advances by one (wrapping at 24) and each light i shows the "lit" frame (ROOMOBJ_DENGDAI2_) when
    /// row[col]=1, else the "dim" frame (ROOMOBJ_DENGDAI1_). Reproduced from
    /// <c>StageScene_UpdatePatternEmitters_004b1eb0</c> (029_scene_004ad250.c):
    /// <c>tex = texArray[ pattern[row*8 + col] ]</c> drawn onto billboards 5..12, col 0..7 = GUANG1..GUANG8, on a
    /// 0x96-ms (150 ms) timer wrapping at 0x18 (24). The two-entry texArray is loaded from GUANG1's folder
    /// (PTR_s_roomobj_dengdai1__dds): index 0 = ROOMOBJ_DENGDAI1_ (dim), index 1 = ROOMOBJ_DENGDAI2_ (lit).
    ///
    /// The 24-row loop (≈3.6 s) is three phases: a left→right chase (rows 0..7, one light at a time), a double
    /// all-on/all-off flash (rows 8..15), then alternating every-other-light blinks (rows 16..23). Column order maps to
    /// the lights' baked positions (col 0 = GUANG1). Bytes are 0/1 exactly as stored (verified: 120×0, 72×1).
    /// </summary>
    public static class RoomDengPattern
    {
        public const int Rows = 24;       // 0x18
        public const int Lights = 8;      // GUANG1..GUANG8
        public const float IntervalMs = 150f;   // 0x96 timer period

        // 24 rows, each 8 chars '0'/'1' (col 0 = GUANG1). Verbatim DAT_00552230.
        private static readonly string[] RowStrings =
        {
            "10000000", "01000000", "00100000", "00010000",   // chase: one light sweeps GUANG1 -> GUANG8
            "00001000", "00000100", "00000010", "00000001",
            "11111111", "11111111", "00000000", "00000000",   // double flash: all on x2, all off x2 ...
            "11111111", "11111111", "00000000", "00000000",   // ... all on x2, all off x2
            "01010101", "10101010", "01010101", "10101010",   // alternating every-other-light blink
            "01010101", "10101010", "01010101", "10101010",
        };

        // Parsed once: Lit[row][light] == true -> that light shows the lit (DENGDAI2) frame on that row.
        private static bool[][] _lit;

        public static bool[][] Lit
        {
            get
            {
                if (_lit == null)
                {
                    var t = new bool[Rows][];
                    for (int r = 0; r < Rows; r++)
                    {
                        t[r] = new bool[Lights];
                        var s = RowStrings[r];
                        for (int c = 0; c < Lights && c < s.Length; c++) t[r][c] = s[c] == '1';
                    }
                    _lit = t;
                }
                return _lit;
            }
        }
    }
}
