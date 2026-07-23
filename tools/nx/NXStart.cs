using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Minimal replacement launcher for the abandoned NXPatch private-server client.
// Reproduces ONLY the launch handshake that NXPatch.exe expects
// (LaunchStamp + seal in argv, and the seal mirrored into a named shared-memory
// block) so the game will start. No license / manifest gating.
//
// Values recovered from the original launcher's own code:
//   Gs0  = 0x7AE373C3   Gs1 = 0x3BA8EB43
//   mmap = Local\NXPatchV25Session   (128 bytes)
//   stamp= --nx=_ry8Sfdm40N9wv7tg2dI1eiodUU9WxWkduk0Ib-o
internal static class NXStart
{
    private const uint Gs0 = 0x7AE373C3u;
    private const uint Gs1 = 0x3BA8EB43u;
    private const string MapName = "Local\\NXPatchV25Session";
    private const string LaunchStamp = "--nx=_ry8Sfdm40N9wv7tg2dI1eiodUU9WxWkduk0Ib-o";
    private const string GameExe = "NXPatch.exe";

    private static uint Compute(string nonce)
    {
        uint num = Gs0;
        for (int i = 0; i < 8; i++)
        {
            num ^= (byte)nonce[i];
            num *= 16777619u;
            num = (num << 5) | (num >> 27);
        }
        num ^= Gs1;
        return (num << 7) | (num >> 25);
    }

    private static string CreateSeal()
    {
        byte[] rnd = new byte[4];
        using (var rng = new RNGCryptoServiceProvider())
            rng.GetBytes(rnd);
        string nonce = BitConverter.ToUInt32(rnd, 0).ToString("X8", CultureInfo.InvariantCulture);
        return "--nxs=" + nonce + Compute(nonce).ToString("X8", CultureInfo.InvariantCulture);
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(baseDir, GameExe);
            if (!File.Exists(exe))
            {
                MessageBox.Show("NXPatch.exe not found next to this launcher.",
                    "NXStart", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            string seal = CreateSeal();

            // Mirror the seal into shared memory exactly as the original did:
            // 128-byte block, zero-filled, seal written as ASCII + NUL (max 127).
            MemoryMappedFile map = MemoryMappedFile.CreateOrOpen(MapName, 128L, MemoryMappedFileAccess.ReadWrite);
            using (var acc = map.CreateViewAccessor(0L, 128L, MemoryMappedFileAccess.Write))
            {
                acc.WriteArray(0L, new byte[128], 0, 128);
                byte[] bytes = Encoding.ASCII.GetBytes(seal + "\0");
                acc.WriteArray(0L, bytes, 0, Math.Min(bytes.Length, 127));
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = baseDir,
                Arguments = LaunchStamp + " " + seal,
                UseShellExecute = false
            };
            Process.Start(psi);

            // Keep the shared-memory block alive long enough for the game to read it.
            Thread.Sleep(8000);
            map.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unable to start game:\n" + ex.Message,
                "NXStart", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
