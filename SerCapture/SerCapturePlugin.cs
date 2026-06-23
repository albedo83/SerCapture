using System.ComponentModel.Composition;
using System.Threading.Tasks;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace SerCapture {
    /// <summary>
    /// Plugin manifest. PluginBase populates the manifest metadata from the assembly attributes in
    /// Properties/AssemblyInfo.cs; NINA discovers the plugin through this IPluginManifest export.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class SerCapturePlugin : PluginBase {

        [ImportingConstructor]
        public SerCapturePlugin() {
        }

        public override Task Teardown() {
            // Graceful NINA shutdown while a recording is still open: finalize the .ser (trailer +
            // FrameCount) and release the handle.
            SerRecorder.Finish();
            return base.Teardown();
        }
    }
}
