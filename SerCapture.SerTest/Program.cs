using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

failures += RecorderTests(Path.Combine(outDir, "recorder_tests"));

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

// SerRecorder regressions: file-name collision, invalid filter name, session left open by another sequence.
static int RecorderTests(string dir) {
    Console.WriteLine("=== SerRecorder ===");
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    Directory.CreateDirectory(dir);

    int fails = 0;
    var frame = new ushort[Width * Height];
    long frameBytes = (long)Width * Height * 2;

    // Collision: an existing file with the same name (same filter, same second) is never overwritten.
    var now = new DateTime(2026, 10, 25, 21, 0, 0);
    var p1 = SerRecorder.BuildFilePath(dir, "L", now);
    File.WriteAllBytes(p1, new byte[] { 42 });
    var p2 = SerRecorder.BuildFilePath(dir, "L", now);
    fails += Check("collision gets a new name", p2 != p1 && p2.EndsWith("_2.ser"), Path.GetFileName(p2));
    File.Delete(p1);

    // Two back-to-back sessions (normally within the same second) give two files.
    var owner = new object();
    for (int s = 0; s < 2; s++) {
        SerRecorder.Begin(dir, "L", owner);
        SerRecorder.WriteFrame(owner, frame, Width, Height, SerColorId.Mono, "", "", "");
        SerRecorder.Finish();
    }
    int files = Directory.GetFiles(dir, "*.ser").Length;
    fails += Check("back-to-back sessions keep both files", files == 2, $"{files} file(s)");

    // Invalid characters in the filter name: frame is written, file stays in the output folder.
    foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
    string? written = null;
    try {
        SerRecorder.Begin(dir, "Ha/OIII:7nm", owner);
        SerRecorder.WriteFrame(owner, frame, Width, Height, SerColorId.Mono, "", "", "");
        SerRecorder.Finish();
        var all = Directory.GetFiles(dir, "*.ser");
        written = all.Length == 1 ? all[0] : null;
    } catch (Exception ex) {
        written = "exception: " + ex.Message;
    }
    fails += Check("invalid filter name sanitized", written != null && File.Exists(written)
                   && Path.GetFileName(written).StartsWith("SER_Ha_OIII_7nm_"), written ?? "no file");

    // Session left open by an aborted sequence A: a frame from sequence B is refused, A's file is
    // finalized (FrameCount + trailer) and its handle released.
    foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
    var seqA = new object();
    var seqB = new object();
    SerRecorder.Begin(dir, "L", seqA);
    for (int i = 0; i < 3; i++) SerRecorder.WriteFrame(seqA, frame, Width, Height, SerColorId.Mono, "", "", "");
    bool refused = false;
    try {
        SerRecorder.WriteFrame(seqB, frame, Width, Height, SerColorId.Mono, "", "", "");
    } catch (Exception) {
        refused = true;
    }
    fails += Check("frame from another sequence refused", refused && !SerRecorder.IsRecording, refused ? "refused" : "accepted");
    var aFile = Directory.GetFiles(dir, "*.ser")[0];
    long aSize = -1;
    int aCount = -1;
    try {
        using var fs = new FileStream(aFile, FileMode.Open, FileAccess.Read, FileShare.None); // fails if still open
        aSize = fs.Length;
        var hdr = new byte[178];
        fs.ReadExactly(hdr);
        aCount = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(38, 4));
    } catch (IOException) { }
    fails += Check("stale file finalized and closed", aCount == 3 && aSize == 178 + 3 * frameBytes + 3 * 8,
                   $"FrameCount={aCount}, size={aSize}");

    // Split by frame count: 5 frames, max 2 per file → 2 + 2 + 1.
    var counts = RecordAndCount(dir, owner, Enumerable.Repeat<string?>(null, 5), maxFrames: 2);
    fails += Check("split by frame count", counts == "1,2,2", counts);

    // Split by size: room for exactly 2 frames (header + 2 × (frame + trailer tick)) → 2 + 1.
    long maxBytes = 178 + 2 * (frameBytes + 8);
    counts = RecordAndCount(dir, owner, Enumerable.Repeat<string?>(null, 3), maxBytes: maxBytes);
    long biggest = Directory.GetFiles(dir, "*.ser").Max(f => new FileInfo(f).Length);
    fails += Check("split by size", counts == "1,2" && biggest <= maxBytes, $"{counts}, largest {biggest} B (max {maxBytes})");

    // Meridian flip: new file when the side of pier changes; unknown side (null) never splits.
    var sides = new string?[] { "pierEast", null, "pierEast", "pierWest", "pierWest" };
    counts = RecordAndCount(dir, owner, sides, flip: true);
    fails += Check("new file after flip", counts == "2,3", counts);
    counts = RecordAndCount(dir, owner, sides, flip: false);
    fails += Check("single file when flip option off", counts == "5", counts);

    Console.WriteLine();
    return fails;
}

// Records one frame per pier-side entry in a clean folder; returns the sorted FrameCounts of the files ("1,2,2").
static string RecordAndCount(string dir, object owner, IEnumerable<string?> pierSides,
                             int maxFrames = 0, long maxBytes = 0, bool flip = false) {
    foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
    var frame = new ushort[Width * Height];
    SerRecorder.Begin(dir, "L", owner, maxFrames, maxBytes, flip);
    foreach (var side in pierSides) {
        SerRecorder.WriteFrame(owner, frame, Width, Height, SerColorId.Mono, "", "", "", side);
    }
    SerRecorder.Finish();
    return string.Join(",", Directory.GetFiles(dir, "*.ser")
        .Select(f => BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(f).AsSpan(38, 4)))
        .OrderBy(c => c));
}

static int Check(string name, bool ok, string actual) {
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {actual}");
    return ok ? 0 : 1;
}
