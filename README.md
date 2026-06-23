# SER Capture — a NINA plugin

> Record your normal NINA imaging into a single `.ser` file (lucky imaging) instead of a pile
> of FITS — by replacing just the `Take Exposure` instruction.

**SER Capture** lets you keep your usual advanced-sequencer setup (loops, filter changes,
dithering, autofocus, meridian flip — all the native triggers) and only swap the capture step:
drop **`Capture SER Frame`** in place of `Take Exposure`, between **`Start SER Recording`** and
**`Stop SER Recording`**. Every frame is appended to one [SER](https://free-astro.org/index.php/SER)
video file — the format expected by lucky-imaging / planetary stacking tools (Siril, PIPP,
AutoStakkert, SER Player).

## Why

NINA excels at deep-sky imaging, where each exposure becomes a calibrated FITS file. For
**lucky imaging** you instead want hundreds or thousands of raw frames bundled into a single
SER file. `Capture SER Frame` captures and writes the raw frame **directly** — no PrepareImage,
no star detection, no FITS — so the per-frame overhead stays low enough for lucky imaging, while
the rest of the sequence (and every native trigger) is untouched.

## Instructions

| Instruction | Role |
|---|---|
| **Start SER Recording** | Opens a new `.ser` (output folder field). Place before the capture loop. |
| **Capture SER Frame** | Drop-in replacement for `Take Exposure`: captures one raw frame into the open `.ser`. Exposure / gain / offset / binning fields. |
| **Stop SER Recording** | Writes the trailer, patches the final frame count and closes the file. |

## Features

- **Drop-in**: `Capture SER Frame` replaces `Take Exposure`; the loop, conditions and **all native
  triggers** (meridian flip, autofocus, dither, …) work exactly as before. It implements
  `IExposureItem` and registers one LIGHT image-history entry per frame, so "after N exposures"
  triggers count frames and fire between them — including in the middle of one `.ser`.
- **Lean / fast**: no PrepareImage, no star detection, no FITS write per frame — suited to lucky imaging.
- Streams **raw, undebayered** 16-bit frames — the SER `ColorID` carries the Bayer pattern so the
  stacker debayers later.
- Correct SER header: `LUCAM-RECORDER`, little-endian data (flag `0`), `ColorID` from the camera’s
  sensor type, `FrameCount` patched **every frame**, per-frame UTC timestamp trailer on finalize.
- **Crash-safe**: the `.ser` is valid at any moment (frame count patched + flushed each frame), so an
  aborted sequence or a NINA crash still leaves an openable file (only the optional trailer is missing).
- **Cadence log**: each frame logs capture/write times and the achieved fps (status bar + NINA log).

## How it fits into a sequence

```
Start SER Recording                 (output folder)
Loop container        [triggers: Meridian Flip, Autofocus After N, Dither After N — native]
  └─ Capture SER Frame              (replaces Take Exposure)
Stop SER Recording
```

A meridian flip in the middle keeps writing to the **same** `.ser` (post-flip frames are rotated
180°, to be handled at processing time).

## Status

| Phase | Description | State |
|------:|-------------|:-----:|
| 0 | Strategy (build from scratch — *Lucky Imaging* plugin is closed-source) | ✅ |
| 1 | Plugin skeleton | ✅ |
| 2 | `SerWriter` + standalone format tests | ✅ |
| 3 | Capture wired to the camera/imaging mediators | ✅ |
| 4 | UI (DataTemplate) + validation | ✅ |
| 5 | Start/Capture/Stop recording — drop-in for Take Exposure, native triggers, no FITS | ✅ |
| 6 | Field validation on real cameras (ASI294MC Pro, ToupTek G3M2210M) | ⬜ |

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
