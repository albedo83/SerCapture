using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;

namespace SerCapture.Sequencer {
    /// <summary>
    /// Stops SER recording: writes the trailer, patches the final FrameCount and closes the .ser file.
    /// Place this after your capture block.
    /// </summary>
    [ExportMetadata("Name", "Stop SER Recording")]
    [ExportMetadata("Description", "Finalize and close the .ser file opened by Start SER Recording")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Camera")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class StopSerRecording : SequenceItem {

        [ImportingConstructor]
        public StopSerRecording() {
        }

        public StopSerRecording(StopSerRecording copyMe) : this() {
            CopyMetaData(copyMe);
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            SerRecorder.Finish();
            return Task.CompletedTask;
        }

        public override object Clone() => new StopSerRecording(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(StopSerRecording)}";
    }
}
