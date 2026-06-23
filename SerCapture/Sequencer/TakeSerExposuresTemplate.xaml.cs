using System.ComponentModel.Composition;
using System.Windows;

namespace SerCapture.Sequencer {
    /// <summary>
    /// Exports the DataTemplate that NINA uses to render the "Take SER Exposures" instruction
    /// in the advanced sequencer. Discovered through the exported <see cref="ResourceDictionary"/>.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class TakeSerExposuresTemplate : ResourceDictionary {
        public TakeSerExposuresTemplate() {
            InitializeComponent();
        }
    }
}
