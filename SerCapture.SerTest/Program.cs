using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using SerCapture.Ser;

// Console harness that de-risks the SER format independently of NINA:
// generate synthetic .ser files, then read them back and assert the structure.
// Manual step afterwards: open the files in Siril and PIPP/AutoStakkert.

const int Width = 64;
const int Height = 48;
const int Frames = 10;

var outDir = AppContext.BaseDirectory;
int failures = 0;

failures += GenerateAndVerify(
    Path.Combine(outDir, "test_mono.ser"),
    SerColorId.Mono,
    pixel: (x, y, f) => (ushort)(((long)x * 65535 / Width + f * 1000) & 0xFFFF)); // gradient + per-frame drift

failures += GenerateAndVerify(
    Path.Combine(outDir, "test_rggb.ser"),
    SerColorId.BayerRggb,
    pixel: (x, y, f) => {
        // Crude raw Bayer pattern: R high, G mid, B low — left undebayered on purpose.
        bool evenRow = (y & 1) == 0, evenCol = (x & 1) == 0;
        ushort v = evenRow ? (evenCol ? (ushort)60000 : (ushort)30000)
                           : (evenCol ? (ushort)30000 : (ushort)8000);
        return (ushort)((v + f * 200) & 0xFFFF);
    });

Console.WriteLine(failures == 0
    ? "\nALL STRUCTURAL CHECKS PASSED. Now open the .ser files in Siril and PIPP/AutoStakkert."
    : $"\n{failures} CHECK(S) FAILED.");
return failures == 0 ? 0 : 1;

static int GenerateAndVerify(string path, SerColorId colorId, Func<int, int, int, ushort> pixel) {
    Console.WriteLine($"=== {Path.GetFileName(path)} ({colorId}) ===");

    var baseUtc = new DateTime(2026, 6, 22, 20, 0, 0, DateTimeKind.Utc);
    var frame = new ushort[Width * Height];

    using (var ser = new SerWriter(path, Width, Height, colorId,
                                   observer: "TestObs", instrument: "SimCam", telescope: "TestScope")) {
        for (int f = 0; f < Frames; f++) {
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    frame[y * Width + x] = pixel(x, y, f);
            ser.WriteFrame(frame, baseUtc.AddSeconds(f));
        }
    }

    int fails = 0;
    byte[] bytes = File.ReadAllBytes(path);

    long expectedSize = 178L + (long)Width * Height * 2 * Frames + 8L * Frames;
    fails += Check("file size", bytes.LongLength == expectedSize, $"{bytes.LongLength} (expected {expectedSize})");

    string fileId = Encoding.ASCII.GetString(bytes, 0, 14);
    fails += Check("FileID", fileId == "LUCAM-RECORDER", $"'{fileId}'");

    int colorIdRead = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
    fails += Check("ColorID", colorIdRead == (int)colorId, colorIdRead.ToString());

    int littleEndian = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
    fails += Check("LittleEndian flag", littleEndian == 0, $"{littleEndian} (expected 0 = little-endian data)");

    int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(26, 4));
    int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(30, 4));
    fails += Check("dimensions", width == Width && height == Height, $"{width}x{height}");

    int depth = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(34, 4));
    fails += Check("PixelDepth", depth == 16, depth.ToString());

    int frameCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(38, 4));
    fails += Check("FrameCount (patched)", frameCount == Frames, frameCount.ToString());

    // First trailer timestamp must match the first frame's UTC and be a plausible recent date.
    long trailerStart = 178L + (long)Width * Height * 2 * Frames;
    long firstTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)trailerStart, 8));
    var firstDate = new DateTime(firstTicks, DateTimeKind.Utc);
    fails += Check("first trailer timestamp", firstDate == baseUtc, firstDate.ToString("o"));
    fails += Check("date plausible (>2020)", firstDate.Year is > 2020 and < 2100, firstDate.Year.ToString());

    long lastTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)(trailerStart + 8L * (Frames - 1)), 8));
    fails += Check("last trailer timestamp", new DateTime(lastTicks, DateTimeKind.Utc) == baseUtc.AddSeconds(Frames - 1),
                   new DateTime(lastTicks, DateTimeKind.Utc).ToString("o"));

    Console.WriteLine();
    return fails;
}

static int Check(string name, bool ok, string actual) {
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {actual}");
    return ok ? 0 : 1;
}
