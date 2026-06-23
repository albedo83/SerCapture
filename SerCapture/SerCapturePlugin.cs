using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace SerCapture {
    /// <summary>
    /// Plugin manifest. <see cref="PluginBase"/> populates the manifest metadata from the
    /// assembly attributes in Properties/AssemblyInfo.cs. NINA discovers the plugin through
    /// this <see cref="IPluginManifest"/> export.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class SerCapturePlugin : PluginBase {

        [ImportingConstructor]
        public SerCapturePlugin() {
        }
    }
}
