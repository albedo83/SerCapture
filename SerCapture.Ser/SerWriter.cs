using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SerCapture.Ser {
    /// <summary>
    /// Streams a sequence of 16-bit raw frames into a single .ser file (lucky-imaging format).
    /// Independent of NINA so it can be unit/console tested in isolation.
    ///
    /// Layout: 178-byte header + raw frame data + trailer (one int64 UTC tick per frame).
    /// Frames are written little-endian (native x86); the header endianness field (offset 22)
    /// is therefore 0 — see DECISIONS.md §5 ("champ maudit").
    /// </summary>
    public sealed class SerWriter : IDisposable {
        private const int HeaderSize = 178;
        private const int FrameCountOffset = 38;
        private const int LittleEndianValue = 0; // Siril SER_LITTLE_ENDIAN = 0 for little-endian data

        private static readonly byte[] FileId = Encoding.ASCII.GetBytes("LUCAM-RECORDER"); // exactly 14 bytes

        private readonly FileStream _stream;
        private readonly int _pixelsPerFrame;
        private readonly List<long> _timestamps = new();
        private readonly bool _patchFrameCountEachFrame;
        private int _frameCount;
        private bool _finalized;

        /// <param name="path">Output .ser file path (overwritten if it exists).</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="colorId">Sensor color pattern (MONO for mono / raw Bayer pattern for OSC).</param>
        /// <param name="observer">Observer name (header, 40 bytes).</param>
        /// <param name="instrument">Camera name (header, 40 bytes).</param>
        /// <param name="telescope">Telescope name (header, 40 bytes).</param>
        /// <param name="patchFrameCountEachFrame">
        /// When true, the FrameCount field is patched after every frame so the file stays a valid SER
        /// even if <see cref="FinalizeFile"/> is never called (e.g. a sequence aborted between a
        /// "Start SER Session" and "Finalize SER Session"). The timestamp trailer is still only
        /// written on finalize. Adds one small seek per frame.
        /// </param>
        public SerWriter(string path, int width, int height, SerColorId colorId,
                         string observer = "", string instrument = "", string telescope = "",
                         bool patchFrameCountEachFrame = false) {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            _patchFrameCountEachFrame = patchFrameCountEachFrame;
            _pixelsPerFrame = width * height;
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read,
                                     bufferSize: 1 << 20);
            WriteHeader(width, height, colorId, observer, instrument, telescope);
        }

        private void WriteHeader(int width, int height, SerColorId colorId,
                                 string observer, string instrument, string telescope) {
            var h = new byte[HeaderSize];
            Buffer.BlockCopy(FileId, 0, h, 0, FileId.Length);   //   0  FileID[14]  "LUCAM-RECORDER"
            WriteI32(h, 14, 0);                                 //  14  LuID = 0
            WriteI32(h, 18, (int)colorId);                      //  18  ColorID
            WriteI32(h, 22, LittleEndianValue);                 //  22  LittleEndian (0 = little-endian data)
            WriteI32(h, 26, width);                             //  26  ImageWidth
            WriteI32(h, 30, height);                            //  30  ImageHeight
            WriteI32(h, 34, 16);                                //  34  PixelDepthPerPlane = 16
            WriteI32(h, 38, 0);                                 //  38  FrameCount (placeholder, patched on finalize)
            WriteString(h, 42, observer, 40);                   //  42  Observer[40]
            WriteString(h, 82, instrument, 40);                 //  82  Instrument[40]
            WriteString(h, 122, telescope, 40);                 // 122  Telescope[40]
            WriteI64(h, 162, DateTime.Now.Ticks);               // 162  DateTime (local ticks)
            WriteI64(h, 170, DateTime.UtcNow.Ticks);            // 170  DateTimeUTC (UTC ticks)
            _stream.Write(h, 0, HeaderSize);
        }

        /// <summary>Appends one raw frame and buffers its UTC timestamp. Never accumulates frames in RAM.</summary>
        public void WriteFrame(ReadOnlySpan<ushort> raw, DateTime utcTimestamp) {
            if (_finalized) throw new InvalidOperationException("SerWriter already finalized.");
            if (raw.Length != _pixelsPerFrame)
                throw new ArgumentException(
                    $"Frame must contain {_pixelsPerFrame} pixels, got {raw.Length}.", nameof(raw));

            // x86/x64 .NET is little-endian: the ushort span's bytes are already little-endian.
            _stream.Write(MemoryMarshal.AsBytes(raw));
            _timestamps.Add(utcTimestamp.Ticks);
            _frameCount++;

            if (_patchFrameCountEachFrame) {
                long end = _stream.Position;
                _stream.Seek(FrameCountOffset, SeekOrigin.Begin);
                Span<byte> fc = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(fc, _frameCount);
                _stream.Write(fc);
                _stream.Seek(end, SeekOrigin.Begin);
            }
        }

        /// <summary>
        /// Writes the trailer (UTC ticks per frame), patches FrameCount at offset 38 and flushes.
        /// Safe to call on cancellation: the file is valid with the frames written so far.
        /// Named FinalizeFile (not Finalize) because Finalize is reserved by the GC in C#.
        /// </summary>
        public void FinalizeFile() {
            if (_finalized) return;

            Span<byte> buf = stackalloc byte[8];
            foreach (var ticks in _timestamps) {
                BinaryPrimitives.WriteInt64LittleEndian(buf, ticks);
                _stream.Write(buf);
            }

            _stream.Seek(FrameCountOffset, SeekOrigin.Begin);
            Span<byte> fc = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(fc, _frameCount);
            _stream.Write(fc);

            _stream.Flush();
            _finalized = true;
        }

        public void Dispose() {
            if (!_finalized) FinalizeFile();
            _stream.Dispose();
        }

        private static void WriteI32(byte[] b, int offset, int value) =>
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset, 4), value);

        private static void WriteI64(byte[] b, int offset, long value) =>
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(offset, 8), value);

        private static void WriteString(byte[] b, int offset, string? s, int maxLen) {
            if (string.IsNullOrEmpty(s)) return; // remaining bytes stay zero (null padding)
            var bytes = Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, 0, b, offset, Math.Min(bytes.Length, maxLen));
        }
    }
}
