using System.IO;
using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 框架層 <c>[uint32 LE len][byte kind][payload]</c> 的測試。
    ///
    /// 這裡最重要的一條是「超長 length 必須在 allocate 之前被拒」—— 那是整個 server 唯一的
    /// OOM 防線。其餘的截斷/EOF 區分則是為了讓連線層能正確分辨「對方正常收線」與「對方掛了」。
    /// </summary>
    public class NetFrameTests
    {
        // ---- round-trip ----

        [Test]
        public void Json_Frame_Round_Trips()
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("{\"t\":\"ping\"}");
            var ms = new MemoryStream();
            NetFrame.Write(ms, NetLimits.FrameKindJson, payload);
            ms.Position = 0;

            byte kind;
            byte[] got;
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryRead(ms, out kind, out got));
            Assert.AreEqual(NetLimits.FrameKindJson, kind);
            Assert.AreEqual(payload, got);
        }

        [Test]
        public void Empty_Payload_Round_Trips_As_Zero_Length_Not_Null()
        {
            // 長度 0 的 frame 是合法的(例如 leaveRoom 之類不帶欄位的訊息可能被編成空 payload)。
            // 收端拿到的必須是長度 0 的陣列而不是 null —— 否則呼叫端每個地方都要多一個 null 檢查。
            var ms = new MemoryStream();
            NetFrame.Write(ms, NetLimits.FrameKindJson, new byte[0]);
            ms.Position = 0;

            byte kind;
            byte[] got;
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryRead(ms, out kind, out got));
            Assert.IsNotNull(got);
            Assert.AreEqual(0, got.Length);
        }

        [Test]
        public void Two_Frames_Of_Different_Kinds_Read_Back_In_Order()
        {
            // control 訊息與檔案 chunk 走同一條連線、共用同一個 reader —— 混流必須正確。
            var json = System.Text.Encoding.UTF8.GetBytes("{\"t\":\"blobUploadDone\"}");
            var chunk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

            var ms = new MemoryStream();
            NetFrame.Write(ms, NetLimits.FrameKindJson, json);
            NetFrame.Write(ms, NetLimits.FrameKindChunk, chunk);
            ms.Position = 0;

            byte k1, k2;
            byte[] p1, p2;
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryRead(ms, out k1, out p1));
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryRead(ms, out k2, out p2));

            Assert.AreEqual(NetLimits.FrameKindJson, k1);
            Assert.AreEqual(json, p1);
            Assert.AreEqual(NetLimits.FrameKindChunk, k2);
            Assert.AreEqual(chunk, p2);
        }

        [Test]
        public void WriteHeader_Puts_Length_Little_Endian()
        {
            // wire format 寫死 little-endian(不用 BitConverter,那會跟著平台 endianness)。
            // 這裡只驗位元組排列，所以刻意用一個四個 byte 都不同的值 —— 它超過 payload 上限，
            // 但 WriteHeader 不做上限檢查(檢查是收端 TryParseHeader 的責任),所以沒問題。
            var buf = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(buf, 0, 0x01020304, NetLimits.FrameKindChunk);

            Assert.AreEqual(0x04, buf[0], "低位位元組要在最前面");
            Assert.AreEqual(0x03, buf[1]);
            Assert.AreEqual(0x02, buf[2]);
            Assert.AreEqual(0x01, buf[3]);
            Assert.AreEqual(NetLimits.FrameKindChunk, buf[4]);
        }

        [Test]
        public void Header_Round_Trips_Within_The_Limit()
        {
            // 合法長度的 round-trip。用 0x0002A5 = 169 KiB(在 256 KiB 上限內)。
            const int len0 = 0x0002A5;
            var buf = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(buf, 0, len0, NetLimits.FrameKindChunk);

            int len;
            byte kind;
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryParseHeader(buf, 0, out len, out kind));
            Assert.AreEqual(len0, len);
            Assert.AreEqual(NetLimits.FrameKindChunk, kind);
        }

        // ---- 🔴 OOM 防線 ----

        [Test]
        public void Oversized_Length_Is_Rejected_By_Header_Parse()
        {
            // 上限 +1 就要擋。呼叫端是「先 TryParseHeader 通過才 allocate payloadLen」，
            // 所以這條通過就代表永遠不會為了一個過大的宣告長度去配置記憶體。
            var buf = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(buf, 0, NetLimits.MaxFramePayload + 1, NetLimits.FrameKindJson);

            int len;
            byte kind;
            Assert.AreEqual(FrameStatus.TooLarge, NetFrame.TryParseHeader(buf, 0, out len, out kind));
        }

        [Test]
        public void Length_At_The_Limit_Is_Accepted()
        {
            var buf = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(buf, 0, NetLimits.MaxFramePayload, NetLimits.FrameKindJson);

            int len;
            byte kind;
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryParseHeader(buf, 0, out len, out kind));
            Assert.AreEqual(NetLimits.MaxFramePayload, len);
        }

        [Test]
        public void Length_With_High_Bit_Set_Does_Not_Wrap_To_Negative()
        {
            // 🔴 這是最容易寫錯的一條。若實作直接把 4 個 byte 當 int 讀，0xFFFFFFFF 會變成 -1，
            // 而 `-1 > MaxFramePayload` 是 false —— 檢查就被繞過了，接著 `new byte[-1]` 拋
            // OverflowException,或更糟:某些寫法會變成長度 0 然後把後面的位元組錯位解讀。
            // 實作刻意先用 uint 比大小才轉 int。
            var buf = new byte[NetFrame.HeaderBytes];
            buf[0] = 0xFF; buf[1] = 0xFF; buf[2] = 0xFF; buf[3] = 0xFF;
            buf[4] = NetLimits.FrameKindJson;

            int len;
            byte kind;
            Assert.AreEqual(FrameStatus.TooLarge, NetFrame.TryParseHeader(buf, 0, out len, out kind));
            Assert.AreEqual(0, len, "被拒的 header 不該回傳可用的長度");
        }

        [Test]
        public void Oversized_Length_On_A_Stream_Returns_TooLarge_Without_Reading_Payload()
        {
            // 整條路徑的驗證:stream 上只有 header(沒有 payload)。如果實作先 allocate 再讀，
            // 這裡會變成 Truncated;正確的實作應該在讀 payload 之前就回 TooLarge。
            var buf = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(buf, 0, NetLimits.MaxFramePayload + 999, NetLimits.FrameKindJson);
            var ms = new MemoryStream(buf);

            byte kind;
            byte[] payload;
            Assert.AreEqual(FrameStatus.TooLarge, NetFrame.TryRead(ms, out kind, out payload));
            Assert.IsNull(payload);
        }

        [Test]
        public void Unknown_Kind_Is_Rejected()
        {
            // 未知的 kind 代表對方跟我們的協定認知不同 —— 與其猜，不如當成錯誤。
            var buf = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(buf, 0, 4, 99);

            int len;
            byte kind;
            Assert.AreEqual(FrameStatus.BadKind, NetFrame.TryParseHeader(buf, 0, out len, out kind));
        }

        // ---- EOF vs 截斷 ----

        [Test]
        public void Clean_Eof_At_Frame_Boundary_Is_Eof_Not_Truncated()
        {
            // 對方正常關連線(在 frame 邊界上)—— 這是正常的離線，不該被記成錯誤。
            var ms = new MemoryStream(new byte[0]);

            byte kind;
            byte[] payload;
            Assert.AreEqual(FrameStatus.Eof, NetFrame.TryRead(ms, out kind, out payload));
        }

        [Test]
        public void Partial_Header_Is_Truncated()
        {
            // header 讀一半就斷 = 對方掛了或網路斷,要跟正常收線區分開來。
            var ms = new MemoryStream(new byte[] { 0x10, 0x00 });

            byte kind;
            byte[] payload;
            Assert.AreEqual(FrameStatus.Truncated, NetFrame.TryRead(ms, out kind, out payload));
        }

        [Test]
        public void Partial_Payload_Is_Truncated()
        {
            // 宣告 10 bytes 但只給 3 bytes。
            var header = new byte[NetFrame.HeaderBytes];
            NetFrame.WriteHeader(header, 0, 10, NetLimits.FrameKindChunk);
            var ms = new MemoryStream();
            ms.Write(header, 0, header.Length);
            ms.Write(new byte[] { 1, 2, 3 }, 0, 3);
            ms.Position = 0;

            byte kind;
            byte[] payload;
            Assert.AreEqual(FrameStatus.Truncated, NetFrame.TryRead(ms, out kind, out payload));
            Assert.IsNull(payload, "沒讀完的 payload 不該交出去");
        }

        [Test]
        public void Read_Handles_Streams_That_Return_Partial_Reads()
        {
            // 🔴 TCP 不保證一次 Read 就拿到整個訊息。NetworkStream 很常只給你一部分，
            // 尤其訊息長度跨過 MTU 的時候。忘記迴圈讀滿的話就會得到那種「偶爾才發生、
            // 而且只在特定長度才重現」的 bug。這裡用一個每次只吐 1 byte 的 stream 逼出來。
            var payload = System.Text.Encoding.UTF8.GetBytes("{\"t\":\"roomState\",\"rev\":42}");
            var whole = NetFrame.Encode(NetLimits.FrameKindJson, payload);
            var drip = new OneByteAtATimeStream(whole);

            byte kind;
            byte[] got;
            Assert.AreEqual(FrameStatus.Ok, NetFrame.TryRead(drip, out kind, out got));
            Assert.AreEqual(payload, got);
        }

        /// <summary>每次 Read 最多吐 1 byte 的 stream,用來模擬 NetworkStream 的部分讀取。</summary>
        private sealed class OneByteAtATimeStream : Stream
        {
            private readonly byte[] _data;
            private int _pos;

            public OneByteAtATimeStream(byte[] data) { _data = data; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos >= _data.Length || count <= 0) return 0;
                buffer[offset] = _data[_pos++];
                return 1;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get { return _pos; } set { throw new System.NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new System.NotSupportedException(); }
            public override void SetLength(long value) { throw new System.NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new System.NotSupportedException(); }
        }
    }
}
