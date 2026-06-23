using System;
using System.IO;
using SerCapture.Ser;

namespace SerCapture {
    /// <summary>
    /// Holds the single open .ser file shared across "Start SER Recording" → "Capture SER Frame"* →
    /// "Stop SER Recording". Static because the recording window spans several sequence items in one
    /// NINA instance (one recording at a time). Frames are written synchronously by Capture SER Frame
    /// (no async save pipeline), so there is no race when stopping. The writer is created lazily on the
    /// first frame; FrameCount is patched every frame so an aborted run still leaves a valid .ser.
    /// Access is serialized by <see cref="Gate"/> as defensive insurance against any re-entrancy.
    /// </summary>
    public static class SerRecorder {
        private static readonly object Gate = new object();
        private static SerWriter _writer;
        private static string _directory;
        private static string _filterName;

        public static bool IsRecording { get; private set; }

        public static void Begin(string directory, string filterName) {
            lock (Gate) {
                CloseDanglingNoLock();
                _directory = directory;
                _filterName = filterName;
                IsRecording = true;
            }
        }

        public static void WriteFrame(ReadOnlySpan<ushort> pixels, int width, int height, SerColorId colorId,
                                      string observer, string instrument, string telescope) {
            lock (Gate) {
                if (!IsRecording) {
                    throw new Exception("No active SER recording. Add a 'Start SER Recording' instruction before 'Capture SER Frame'.");
                }
                if (_writer == null) {
                    Directory.CreateDirectory(_directory);
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var name = string.IsNullOrWhiteSpace(_filterName) ? $"SER_{stamp}.ser" : $"SER_{_filterName}_{stamp}.ser";
                    _writer = new SerWriter(Path.Combine(_directory, name), width, height, colorId,
                                            observer, instrument, telescope, patchFrameCountEachFrame: true);
                }
                _writer.WriteFrame(pixels, DateTime.UtcNow);
            }
        }

        public static void Finish() {
            lock (Gate) {
                CloseDanglingNoLock();
            }
        }

        private static void CloseDanglingNoLock() {
            if (_writer != null) {
                try { _writer.Dispose(); } catch { /* finalizes (trailer + FrameCount); leave no open handle */ }
                _writer = null;
            }
            IsRecording = false;
        }
    }
}
