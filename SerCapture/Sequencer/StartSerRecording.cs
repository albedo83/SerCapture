using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;

namespace SerCapture.Sequencer {
    /// <summary>
    /// Starts recording saved light frames into a single .ser file. Place this before your normal
    /// capture (loop / Take Exposure / triggers); every light saved until "Stop SER Recording" is
    /// appended to the file instead of kept as a FITS.
    /// </summary>
    [ExportMetadata("Name", "Start SER Recording")]
    [ExportMetadata("Description", "Record subsequent light frames into a single .ser file (until Stop SER Recording)")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Camera")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class StartSerRecording : SequenceItem, IValidatable {
        private readonly IProfileService profileService;
        private readonly IFilterWheelMediator filterWheelMediator;
        private string outputDirectory = string.Empty;
        private IList<string> issues = new List<string>();

        [ImportingConstructor]
        public StartSerRecording(IProfileService profileService, IFilterWheelMediator filterWheelMediator) {
            this.profileService = profileService;
            this.filterWheelMediator = filterWheelMediator;
        }

        public StartSerRecording(StartSerRecording copyMe) : this(copyMe.profileService, copyMe.filterWheelMediator) {
            CopyMetaData(copyMe);
            OutputDirectory = copyMe.OutputDirectory;
        }

        [JsonProperty]
        public string OutputDirectory { get => outputDirectory; set { outputDirectory = value; RaisePropertyChanged(); } }

        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var dir = ResolveDirectory();
            Directory.CreateDirectory(dir);
            var filter = filterWheelMediator.GetInfo()?.SelectedFilter?.Name;
            SerRecorder.Begin(dir, filter);
            return Task.CompletedTask;
        }

        public bool Validate() {
            var i = new List<string>();
            var dir = ResolveDirectory();
            if (string.IsNullOrWhiteSpace(dir)) {
                i.Add("No output folder set and the profile image folder is empty.");
            } else {
                // Fail fast before capturing: make sure we can actually create files in the folder.
                try {
                    Directory.CreateDirectory(dir);
                    var probe = Path.Combine(dir, ".sercap_write_test");
                    File.WriteAllBytes(probe, Array.Empty<byte>());
                    File.Delete(probe);
                } catch (Exception ex) {
                    i.Add($"Output folder is not writable: {ex.Message}");
                }
            }
            Issues = i;
            return i.Count == 0;
        }

        private string ResolveDirectory() =>
            string.IsNullOrWhiteSpace(OutputDirectory)
                ? profileService.ActiveProfile.ImageFileSettings.FilePath
                : OutputDirectory;

        public override object Clone() => new StartSerRecording(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(StartSerRecording)}";
    }
}
