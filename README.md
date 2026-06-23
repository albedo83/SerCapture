# SER Capture — a NINA plugin

> Capture a burst of raw frames into a single `.ser` file (lucky imaging) straight from the
> NINA advanced sequencer, instead of a pile of FITS.

**SER Capture** adds a **“Take SER Exposures”** instruction to the [NINA](https://nighttime-imaging.eu/)
advanced sequencer. Each run records *N* raw frames into one [SER](https://free-astro.org/index.php/SER)
video file — the format expected by lucky-imaging / planetary stacking tools
(Siril, PIPP, AutoStakkert, SER Player). The rest of the sequencer keeps doing its job
*around* the instruction: loops, filter changes, dithering, autofocus and meridian flips.

## Why

NINA excels at deep-sky imaging, where each exposure becomes a calibrated FITS file. For
**lucky imaging** you instead want hundreds or thousands of short raw frames bundled into a
single SER file, with no per-frame debayering, stretching or star detection. SER Capture
bypasses that per-image post-processing and streams the raw sensor data directly to disk.

## Features

- One **`Take SER Exposures`** instruction, native to the advanced sequencer.
- Streams **raw, undebayered** 16-bit frames — the SER `ColorID` carries the Bayer pattern so
  the stacker debayers later.
- Correct SER header: `LUCAM-RECORDER`, little-endian data, `ColorID` derived from the camera’s
  sensor type, `FrameCount` patched on completion, and a per-frame UTC timestamp trailer.
- **Cancellation-safe**: stopping mid-capture still produces a valid `.ser` containing the
  frames recorded so far (the frame count is patched, no corrupt file).
- **Streaming writer**: frames are flushed straight to disk; only the timestamp list is kept in RAM.
- Editable settings in the sequencer: exposure, gain, offset, binning, frame count, output folder.
- Validation: warns when the camera is disconnected, the frame count is invalid, or there isn’t
  enough free disk space.

## How it fits into a sequence

One execution of the instruction = one `.ser` file = **one** sequencer “exposure” event, so
triggers (autofocus / dither after *N* exposures) count SER files, not individual frames:

```
Loop until 03:00
 ├─ Switch Filter (Ha)
 ├─ Take SER  (N frames × t sec)     ← this instruction
 ├─ Dither
 └─ [trigger] Autofocus after 1 exposure
```

Each loop = one SER = one dither = optionally one autofocus.

## Status

| Phase | Description | State |
|------:|-------------|:-----:|
| 0 | Strategy (build from scratch — *Lucky Imaging* plugin is closed-source) | ✅ |
| 1 | Plugin skeleton (instruction appears in the sequencer) | ✅ |
| 2 | `SerWriter` + standalone format tests | ✅ |
| 3 | Capture wired to the camera/imaging mediators | ✅ |
| 4 | UI (DataTemplate) + validation | ✅ |
| 5 | Field integration on real cameras (loop / filter / dither / autofocus) | ⬜ |

Validated against the NINA camera simulator: a 640×480 / 20-frame `.ser` opens correctly in
SER Player and PIPP, with a well-formed header and ordered timestamps.

## Requirements

- NINA **3.2.x** (built against the `NINA.Plugin` 3.2.0.9001 NuGet package).
- .NET 8 SDK with the Windows Desktop (WPF) workload, to build from source.

## Build

```powershell
dotnet build -c Release SerCapture.slnx
```

Only `SerCapture.dll` and `SerCapture.Ser.dll` are your own assemblies — the `NINA.*`
dependencies are provided by the running NINA host and must **not** be shipped.

## Install

Copy the two plugin assemblies into NINA’s plugin folder, then restart NINA:

```powershell
$dst = "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\SerCapture"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "SerCapture\bin\Release\net8.0-windows\SerCapture.dll"     $dst -Force
Copy-Item "SerCapture\bin\Release\net8.0-windows\SerCapture.Ser.dll" $dst -Force
```

The instruction then appears under **Camera** in the advanced sequencer.

## Testing the SER writer (no NINA needed)

`SerCapture.Ser` is independent of NINA, so the file format can be de-risked in isolation:

```powershell
dotnet run -c Release --project SerCapture.SerTest
```

This generates `test_mono.ser` and `test_rggb.ser`, then asserts the structure (size,
`FileID`, `ColorID`, endianness flag, patched `FrameCount`, timestamps). Open the files in
Siril and PIPP/AutoStakkert to confirm the image looks right (byte-swapped data would show
as pure noise).

## Project structure

```
SerCapture.Ser/       Standalone SER format library (SerWriter) — no NINA dependency
SerCapture/           The NINA plugin (WPF / MEF, references NINA.Plugin)
SerCapture.SerTest/   Console harness that exercises SerWriter
```

## SER format notes

The SER endianness field at offset 22 is historically ambiguous. This plugin follows Siril’s
de-facto convention (`SER_LITTLE_ENDIAN = 0`): pixel data is written little-endian and the
field is set to `0`. Timestamps use raw .NET `DateTime.Ticks` (100 ns since 0001-01-01), which
is exactly the SER native time format — no epoch conversion. See `DECISIONS.md` for details.

## Acknowledgements

- The [NINA](https://nighttime-imaging.eu/) team and the official
  [plugin template](https://github.com/isbeorn/nina.plugin.template).
- Grischa Hahn’s SER format specification and the
  [Siril SER documentation](https://siril.readthedocs.io/en/latest/file-formats/SER.html).

## License

MPL-2.0.
