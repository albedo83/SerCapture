using System;
using System.IO;

namespace SerCapture.Ser {
    /// <summary>
    /// Holds the single open .ser file shared across "Start SER Recording" → "Capture SER Frame"* →
    /// "Stop SER Recording". Static because the recording window spans several sequence items in one
    /// NINA instance (one recording at a time). Frames are written synchronously by Capture SER Frame
    /// (no async save pipeline), so there is no race when stopping. The writer is created lazily on the
    /// first frame; FrameCount is patched every frame so an aborted run still leaves a valid .ser.
    /// Access is serialized by <see cref="Gate"/> as defensive insurance against any re-entrancy.
    /// Lives here (no NINA dependency) so it can be tested by SerCapture.SerTest.
    /// </summary>
    public static class SerRecorder {
        private const int HeaderBytes = 178;
        private const int TrailerBytesPerFrame = 8;

        private static readonly object Gate = new object();
        private static SerWriter? _writer;
        private static string _directory = string.Empty;
        private static string? _filterName;
        private static object? _owner;
        private static int _maxFramesPerFile;
        private static long _maxFileBytes;
        private static bool _newFileOnPierSideChange;
        private static long _fileBytes;
        private static string? _filePierSide;

        public static bool IsRecording { get; private set; }

        /// <param name="owner">
        /// Identifies the sequence run that opened the session (the plugin passes the sequence root
        /// container). Frames from another owner never go into this file — see <see cref="WriteFrame"/>.
        /// </param>
        /// <param name="maxFramesPerFile">Start a new .ser once a file holds this many frames (0 = no limit).</param>
        /// <param name="maxFileBytes">Start a new .ser before a file would exceed this size, trailer included (0 = no limit).</param>
        /// <param name="newFileOnPierSideChange">Start a new .ser when the mount changes side of pier (meridian flip).</param>
        public static void Begin(string directory, string? filterName, object? owner,
                                 int maxFramesPerFile = 0, long maxFileBytes = 0, bool newFileOnPierSideChange = false) {
            lock (Gate) {
                CloseDanglingNoLock();
                _directory = directory;
                _filterName = filterName;
                _owner = owner;
                _maxFramesPerFile = maxFramesPerFile;
                _maxFileBytes = maxFileBytes;
                _newFileOnPierSideChange = newFileOnPierSideChange;
                IsRecording = true;
            }
        }

        /// <param name="pierSide">Current side of pier, or null when unknown (never triggers a split).</param>
        /// <returns>The path of the .ser file this frame opened, or null if it went into the current file.</returns>
        public static string? WriteFrame(object? owner, ReadOnlySpan<ushort> pixels, int width, int height, SerColorId colorId,
                                         string observer, string instrument, string telescope, string? pierSide = null) {
            lock (Gate) {
                if (IsRecording && !ReferenceEquals(owner, _owner)) {
                    // Session left open by another (aborted) sequence: close it instead of appending foreign frames.
                    CloseDanglingNoLock();
                }
                if (!IsRecording) {
                    throw new Exception("No active SER recording. Add a 'Start SER Recording' instruction before 'Capture SER Frame'.");
                }

                long frameBytes = (long)pixels.Length * sizeof(ushort) + TrailerBytesPerFrame;
                if (_writer != null && MustSplitNoLock(frameBytes, pierSide)) {
                    CloseWriterNoLock();
                }

                string? opened = null;
                if (_writer == null) {
                    Directory.CreateDirectory(_directory);
                    opened = BuildFilePath(_directory, _filterName, DateTime.Now);
                    _writer = new SerWriter(opened, width, height, colorId,
                                            observer, instrument, telescope, patchFrameCountEachFrame: true);
                    _fileBytes = HeaderBytes;
                    _filePierSide = null;
                }
                _writer.WriteFrame(pixels, DateTime.UtcNow);
                _fileBytes += frameBytes;
                _filePierSide ??= pierSide;
                return opened;
            }
        }

        public static void Finish() {
            lock (Gate) {
                CloseDanglingNoLock();
            }
        }

        /// <summary>
        /// SER_[filter_]yyyyMMdd_HHmmss.ser in <paramref name="directory"/>. Characters that are invalid in a
        /// file name are replaced by '_'; an existing file is never reused (suffix _2, _3, …).
        /// </summary>
        public static string BuildFilePath(string directory, string? filterName, DateTime now) {
            var stamp = now.ToString("yyyyMMdd_HHmmss");
            var baseName = string.IsNullOrWhiteSpace(filterName) ? $"SER_{stamp}" : $"SER_{SanitizeFileName(filterName)}_{stamp}";
            var path = Path.Combine(directory, baseName + ".ser");
            for (int n = 2; File.Exists(path); n++) {
                path = Path.Combine(directory, $"{baseName}_{n}.ser");
            }
            return path;
        }

        private static string SanitizeFileName(string s) {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = s.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            return new string(chars);
        }

        // A file always gets at least one frame, so a limit smaller than one frame cannot loop.
        private static bool MustSplitNoLock(long frameBytes, string? pierSide) {
            if (_maxFramesPerFile > 0 && _writer!.FrameCount >= _maxFramesPerFile) return true;
            if (_maxFileBytes > 0 && _fileBytes + frameBytes > _maxFileBytes) return true;
            if (_newFileOnPierSideChange && pierSide != null && _filePierSide != null && pierSide != _filePierSide) return true;
            return false;
        }

        private static void CloseWriterNoLock() {
            if (_writer != null) {
                try { _writer.Dispose(); } catch { /* finalizes (trailer + FrameCount); leave no open handle */ }
                _writer = null;
            }
        }

        private static void CloseDanglingNoLock() {
            CloseWriterNoLock();
            _owner = null;
            IsRecording = false;
        }
    }
}
