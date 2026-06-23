using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] Unique identifier of the plugin (fresh GUID).
[assembly: Guid("d4e38328-7621-4af0-90b0-1213f914c865")]

// [MANDATORY] Assembly versioning — bump for each release build.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// [MANDATORY] Plugin name and short description (surfaced in NINA's plugin list).
[assembly: AssemblyTitle("SER Capture")]
[assembly: AssemblyDescription("Capture a burst of raw frames into a single .ser file from the advanced sequencer.")]

// Manifest metadata.
[assembly: AssemblyCompany("Sébastien Seignier")]
[assembly: AssemblyProduct("SER Capture")]
[assembly: AssemblyCopyright("Copyright © 2026 Sébastien Seignier")]

// Minimum NINA version this plugin is compatible with (matches the installed 3.2.0.9001).
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/albedo83/SerCapture")]

[assembly: ComVisible(false)]
