using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using SerCapture.Ser;

namespace SerCapture.Sequencer {
    /// <summary>
    /// Advanced-sequencer instruction that captures a burst of raw frames into a single .ser file.
    /// One execution = one .ser = one sequencer "exposure" event (triggers count SERs, not frames).
    /// Frames are written RAW (never debayered); the ColorID encodes the Bayer pattern.
    /// </summary>
    [ExportMetadata("Name", "Take SER Exposures")]
    [ExportMetadata("Description", "Capture a burst of raw frames into a single .ser file")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Camera")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class TakeSerExposures : SequenceItem, IValidatable {
        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;

        private double exposureTime = 1.0;
        private int gain = -1;
        private int offset = -1;
        private short binning = 1;
        private int frameCount = 10;
        private string outputDirectory = string.Empty;
        private IList<string> issues = new List<string>();

        [ImportingConstructor]
        public TakeSerExposures(IProfileService profileService,
                                ICameraMediator cameraMediator,
                                IImagingMediator imagingMediator,
                                IFilterWheelMediator filterWheelMediator) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
        }

        public TakeSerExposures(TakeSerExposures copyMe)
            : this(copyMe.profileService, copyMe.cameraMediator, copyMe.imagingMediator, copyMe.filterWheelMediator) {
            CopyMetaData(copyMe);
            ExposureTime = copyMe.ExposureTime;
            Gain = copyMe.Gain;
            Offset = copyMe.Offset;
            Binning = copyMe.Binning;
            FrameCount = copyMe.FrameCount;
            OutputDirectory = copyMe.OutputDirectory;
        }

        [JsonProperty] public double ExposureTime { get => exposureTime; set { exposureTime = value; RaisePropertyChanged(); } }
        [JsonProperty] public int Gain { get => gain; set { gain = value; RaisePropertyChanged(); } }
        [JsonProperty] public int Offset { get => offset; set { offset = value; RaisePropertyChanged(); } }
        [JsonProperty] public short Binning { get => binning; set { binning = value; RaisePropertyChanged(); } }
        [JsonProperty] public int FrameCount { get => frameCount; set { frameCount = value; RaisePropertyChanged(); } }
        [JsonProperty] public string OutputDirectory { get => outputDirectory; set { outputDirectory = value; RaisePropertyChanged(); } }

        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var camInfo = cameraMediator.GetInfo();
            if (camInfo == null || !camInfo.Connected) {
                throw new Exception("SER Capture: camera is not connected.");
            }

            var colorId = MapSensorType(camInfo.SensorType);
            var filter = filterWheelMediator.GetInfo()?.SelectedFilter;
            var sequence = new CaptureSequence(ExposureTime, CaptureSequence.ImageTypes.LIGHT, filter,
                                               new BinningMode(Binning, Binning), 1) {
                Gain = Gain,
                Offset = Offset,
            };

            var path = BuildOutputPath(filter?.Name);
            SerWriter writer = null;
            try {
                for (int i = 0; i < FrameCount; i++) {
                    token.ThrowIfCancellationRequested();

                    var exposure = await imagingMediator.CaptureImage(sequence, token, progress, string.Empty);
                    var imageData = await exposure.ToImageData(progress, token);
                    var pixels = imageData.Data.FlatArray;

                    if (writer == null) {
                        var props = imageData.Properties;
                        var meta = imageData.MetaData;
                        writer = new SerWriter(path, props.Width, props.Height, colorId,
                            observer: meta?.Observer?.Name ?? string.Empty,
                            instrument: camInfo.Name ?? meta?.Camera?.Name ?? string.Empty,
                            telescope: meta?.Telescope?.Name ?? string.Empty);
                    }

                    writer.WriteFrame(pixels, DateTime.UtcNow);
                    progress?.Report(new ApplicationStatus {
                        Source = Name,
                        Status = $"SER frame {i + 1}/{FrameCount}",
                        Progress = i + 1,
                        MaxProgress = FrameCount,
                    });
                }
            } finally {
                // Dispose finalizes the file (patches FrameCount + trailer) even on cancellation/error,
                // so a partial capture still produces a valid .ser.
                writer?.Dispose();
            }
        }

        public bool Validate() {
            var i = new List<string>();
            var camInfo = cameraMediator.GetInfo();

            if (camInfo == null || !camInfo.Connected) {
                i.Add("Camera is not connected.");
            }
            if (FrameCount <= 0) {
                i.Add("Frame count must be greater than 0.");
            }
            if (ExposureTime < 0) {
                i.Add("Exposure time cannot be negative.");
            }

            // Disk-space estimate once the sensor dimensions are known: W*H*2*N + 178 + 8*N.
            if (camInfo != null && camInfo.Connected && FrameCount > 0) {
                long estimate = (long)camInfo.XSize * camInfo.YSize * 2 * FrameCount + 178 + 8L * FrameCount;
                try {
                    var dir = string.IsNullOrWhiteSpace(OutputDirectory)
                        ? profileService.ActiveProfile.ImageFileSettings.FilePath
                        : OutputDirectory;
                    var root = Path.GetPathRoot(Path.GetFullPath(dir));
                    if (!string.IsNullOrEmpty(root)) {
                        var drive = new DriveInfo(root);
                        if (drive.IsReady && drive.AvailableFreeSpace < estimate) {
                            i.Add($"Not enough free disk space: need ~{estimate / (1024 * 1024)} MB, "
                                + $"available {drive.AvailableFreeSpace / (1024 * 1024)} MB.");
                        }
                    }
                } catch {
                    // Ignore disk-probing errors; they should not block the sequence.
                }
            }

            Issues = i;
            return i.Count == 0;
        }

        public override object Clone() {
            return new TakeSerExposures(this);
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(TakeSerExposures)}, Frames: {FrameCount}, Exposure: {ExposureTime}s";
        }

        private string BuildOutputPath(string filterName) {
            var dir = string.IsNullOrWhiteSpace(OutputDirectory)
                ? profileService.ActiveProfile.ImageFileSettings.FilePath
                : OutputDirectory;
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var name = string.IsNullOrWhiteSpace(filterName) ? $"SER_{stamp}.ser" : $"SER_{filterName}_{stamp}.ser";
            return Path.Combine(dir, name);
        }

        /// <summary>
        /// Maps NINA's sensor type to a SER ColorID. RAW Bayer patterns are passed through untouched;
        /// a generic OSC "Color" sensor (pattern unspecified by the driver) is assumed RGGB.
        /// </summary>
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
