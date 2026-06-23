using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.ViewModel;
using SerCapture.Ser;

namespace SerCapture.Sequencer {
    /// <summary>
    /// Drop-in replacement for "Take Exposure" that records into the active SER session instead of a
    /// FITS file. It captures one raw frame and writes it straight to the .ser — no PrepareImage, no
    /// star detection, no FITS — so it is light enough for lucky imaging. It implements
    /// <see cref="IExposureItem"/> and adds one LIGHT entry to the image history, so NINA's native
    /// triggers on the loop container (meridian flip, autofocus, dither) count it and fire between
    /// frames, exactly like with Take Exposure.
    /// </summary>
    [ExportMetadata("Name", "Capture SER Frame")]
    [ExportMetadata("Description", "Capture one raw frame into the active SER session (replaces Take Exposure)")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Camera")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class CaptureSerFrame : SequenceItem, IExposureItem, IValidatable {
        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IImageHistoryVM imageHistoryVM;

        private double exposureTime = 1.0;
        private int gain = -1;
        private int offset = -1;
        private BinningMode binning = new BinningMode(1, 1);
        private string imageType = "LIGHT";
        private IList<string> issues = new List<string>();

        // Cadence measurement (instance fields persist across loop iterations of the same item).
        private long prevWriteTimestamp;
        private int frameNo;

        [ImportingConstructor]
        public CaptureSerFrame(IProfileService profileService, ICameraMediator cameraMediator,
                               IImagingMediator imagingMediator, IImageHistoryVM imageHistoryVM) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.imageHistoryVM = imageHistoryVM;
        }

        public CaptureSerFrame(CaptureSerFrame copyMe)
            : this(copyMe.profileService, copyMe.cameraMediator, copyMe.imagingMediator, copyMe.imageHistoryVM) {
            CopyMetaData(copyMe);
            ExposureTime = copyMe.ExposureTime;
            Gain = copyMe.Gain;
            Offset = copyMe.Offset;
            Binning = copyMe.Binning;
            ImageType = copyMe.ImageType;
        }

        [JsonProperty] public double ExposureTime { get => exposureTime; set { exposureTime = value; RaisePropertyChanged(); } }
        [JsonProperty] public int Gain { get => gain; set { gain = value; RaisePropertyChanged(); } }
        [JsonProperty] public int Offset { get => offset; set { offset = value; RaisePropertyChanged(); } }
        [JsonProperty] public BinningMode Binning { get => binning; set { binning = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(Bin)); } }
        [JsonProperty] public string ImageType { get => imageType; set { imageType = value; RaisePropertyChanged(); } }

        /// <summary>Symmetric binning helper for the UI (sets Binning to NxN).</summary>
        public int Bin {
            get => binning?.X ?? 1;
            set { Binning = new BinningMode((short)value, (short)value); }
        }

        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();

            var camInfo = cameraMediator.GetInfo();
            if (camInfo == null || !camInfo.Connected) {
                throw new Exception("SER Capture: camera is not connected.");
            }

            var colorId = MapSensorType(camInfo.SensorType);
            var sequence = new CaptureSequence {
                ExposureTime = ExposureTime,
                ImageType = ImageType,
                Gain = Gain,
                Offset = Offset,
                Binning = Binning,
                TotalExposureCount = 1,
                ProgressExposureCount = 0,
            };

            // Lean path: capture + raw image data only. No PrepareImage / star detection / FITS save.
            var t0 = Stopwatch.GetTimestamp();
            var exposure = await imagingMediator.CaptureImage(sequence, token, progress, string.Empty);
            if (exposure == null) {
                throw new Exception("SER Capture: camera returned no exposure data.");
            }
            var imageData = await exposure.ToImageData(progress, token);
            var pixels = imageData?.Data?.FlatArray;
            if (pixels == null) {
                throw new Exception("SER Capture: no raw pixel data available for this frame.");
            }
            var props = imageData.Properties;
            var meta = imageData.MetaData;
            var tCaptured = Stopwatch.GetTimestamp();

            SerRecorder.WriteFrame(pixels, props.Width, props.Height, colorId,
                observer: meta?.Observer?.Name ?? string.Empty,
                instrument: camInfo.Name ?? meta?.Camera?.Name ?? string.Empty,
                telescope: meta?.Telescope?.Name ?? string.Empty);
            var tWritten = Stopwatch.GetTimestamp();

            // One LIGHT history entry per frame so native "after # exposures" triggers count frames.
            if (ImageType == "LIGHT" || ImageType == "SNAPSHOT") {
                imageHistoryVM.Add(exposure.MetaData.Image.Id, ImageType);
            }

            // Cadence log: capture (expose+download), write, and frame-to-frame period / fps.
            frameNo++;
            double captureMs = Ms(t0, tCaptured);
            double writeMs = Ms(tCaptured, tWritten);
            double periodMs = prevWriteTimestamp == 0 ? 0 : Ms(prevWriteTimestamp, tWritten);
            double fps = periodMs > 0 ? 1000.0 / periodMs : 0;
            prevWriteTimestamp = tWritten;

            var status = periodMs > 0
                ? $"SER frame {frameNo}: {fps:F2} fps (capture {captureMs:F0} ms, write {writeMs:F0} ms)"
                : $"SER frame {frameNo}: capture {captureMs:F0} ms, write {writeMs:F0} ms";
            Logger.Info($"SerCapture {status}");
            progress?.Report(new ApplicationStatus { Source = Name, Status = status });
        }

        private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        public bool Validate() {
            var i = new List<string>();
            var camInfo = cameraMediator.GetInfo();
            if (camInfo == null || !camInfo.Connected) {
                i.Add("Camera is not connected.");
            }
            if (ExposureTime < 0) {
                i.Add("Exposure time cannot be negative.");
            }
            Issues = i;
            return i.Count == 0;
        }

        public override object Clone() => new CaptureSerFrame(this);

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(ExposureTime);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(CaptureSerFrame)}, ExposureTime: {ExposureTime}s, ImageType: {ImageType}";

        private static SerColorId MapSensorType(SensorType sensorType) {
            switch (sensorType) {
                case SensorType.Monochrome: return SerColorId.Mono;
                case SensorType.RGGB: return SerColorId.BayerRggb;
                case SensorType.GRBG: return SerColorId.BayerGrbg;
                case SensorType.GBRG: return SerColorId.BayerGbrg;
                case SensorType.BGGR: return SerColorId.BayerBggr;
                case SensorType.Color: return SerColorId.BayerRggb;
                default: return SerColorId.Mono;
            }
        }
    }
}
