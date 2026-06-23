using System.ComponentModel.Composition;
using System.Windows;

namespace SerCapture.Sequencer {
    /// <summary>
    /// Exports the DataTemplates NINA uses to render the SER Capture instructions in the sequencer.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class SerCaptureTemplates : ResourceDictionary {
        public SerCaptureTemplates() {
            InitializeComponent();
        }
    }
}
