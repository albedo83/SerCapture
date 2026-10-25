# DECISIONS — NINA plugin "SER Capture"

This file freezes the scope, the architecture and the choices already settled. Never contradict a
decision recorded here without flagging it first. The full specification is
`nina-ser-capture-plan.md`; this document records the decisions actually applied.

## 1. Scope

NINA (Nighttime Imaging 'N' Astronomy) plugin that records a burst of raw frames into **a single
`.ser` file** (lucky imaging — primarily **stellar / deep-sky** lucky imaging), instead of a stack of
FITS. The sequencer's loops/triggers/conditions/dither/AF operate **around** the capture.

## 2. Architecture

- The original spec called for a single `SequenceItem` with no container and "one SER = one
  sequencer event"; this was superseded by the Start/Capture/Stop flow (§6).
- Three projects in the solution:
  - `SerCapture.Ser` — standalone library **with no NINA dependency** (`SerWriter`, the format, and
    `SerRecorder` — moved here on 2026-10-25 so it can be tested by `SerTest`). Testable outside
    NINA; this is where the binary format is de-risked.
  - `SerCapture` — the plugin (WPF/MEF, references NINA.Plugin and `SerCapture.Ser`).
  - `SerCapture.SerTest` — console test harness for `SerWriter` and `SerRecorder`.
- Abstraction is earned through use: no speculative hardware abstraction layer.

## 3. Phase 0 — from scratch (settled)

The *Lucky Imaging* plugin (Nick Holland) **is not open source**: no public repository found
(exhaustive web search on 2026-06-22), distributed as a binary through the NINA manifest, and not
installed on the machine. → Built **from scratch** (spec Phases 1→5), no fork.

## 4. Tooling & build

- **Manual scaffolding with `dotnet`** (not the Visual Studio extension). Hand-written SDK-style
  `.csproj` files, CLI build and deployment.
- Reference: NuGet package **`NINA.Plugin` 3.2.0.9001** (meta-package that transitively pulls
  Core/Sequencer/Image/WPF.Base/Plugin.Interfaces/Profile). Version aligned with the **installed
  NINA 3.2.0.9001** (`C:\Program Files\N.I.N.A. ...`).
- Authoritative API source: the NuGet packages + the real sources of the official
  `isbeorn/nina.plugin.template`. **No guessed signatures.**
- Deployment: copy the DLLs into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\SerCapture\`
  (`3.0.0` = plugin API version, not the NINA version; confirmed from installed plugins).

## 5. SER format — hard decisions

- **Endianness (the "cursed field", offset 22).** The spec said "flag = 1" for little-endian data.
  **Decision: we write 0.** Checked in the Siril source (`src/io/ser.h`): `SER_LITTLE_ENDIAN = 0`,
  `SER_BIG_ENDIAN = 1`, and `ser_create_file()` writes `SER_LITTLE_ENDIAN` (= 0) for little-endian
  data. This is the de facto standard (SharpCap/FireCapture/PIPP/AutoStakkert). Pixels are written
  little-endian (native x86) **and** offset 22 = **0**. → Contradicts invariant #3 of the spec,
  flagged here.
- **ColorID** (confirmed from Siril `ser.h`): MONO=0, RGGB=8, GRBG=9, GBRG=10, BGGR=11.
- **PixelDepth (offset 34) = 16, hard-coded.** NINA pixels (`IImageArray.FlatArray`) are raw
  *right-aligned* `ushort` values (a 13/14-bit sensor gives 0..8191 / 0..16383, not rescaled to
  16 bits). We declare 16 (container size, the most common choice). Validated with the simulator
  (13 bits): SER Player/AutoStakkert/PIPP auto-normalize → correct image. Known limitation: a viewer
  that trusts the header without auto-stretch shows a dark image. Future option if needed: write the
  real depth (`IImageData.Properties.BitDepth`) at offset 34.
- **DateTime = raw `DateTime.Ticks`** (100 ns since 0001-01-01), no epoch conversion.
  Offset 162 = local (`DateTime.Now.Ticks`), offset 170 = UTC (`DateTime.UtcNow.Ticks`).
- **FrameCount** written as 0 placeholder (offset 38), patched at the end (including on
  cancellation).
- **Trailer**: `FrameCount × 8` bytes, one little-endian int64 UTC tick per frame.
- **RAW data never debayered**; streamed, the frame stack is never buffered in RAM.
- **Method naming**: the finalization method is `FinalizeFile()`, not `Finalize()` (reserved by the
  GC in C#, cannot be redefined).

## 6. Triggers & "session" flow (redesign vs the plan)

**Actual requirement (2026-06-23)**: N single exposures (e.g. 3600), **one single .ser file**, with
a trigger (AF/dither) running **in the middle** (e.g. at 1800), then resuming in the same SER. This
**contradicts** the plan ("a single SequenceItem, no container" + "1 SER = 1 event"). Flagged and
settled.

**NINA trigger mechanics (decompiled from 3.2.0.9001, certain):**
- `AutofocusAfterExposures.ShouldTrigger` / `DitherAfterExposures.ShouldTrigger` return `false` if
  `nextItem` is not an **`IExposureItem` of type "LIGHT"**.
- The counter reads **`IImageHistoryVM.ImageHistory`** (LIGHT, `Id > lastAutoFocusId`).
- `TakeExposure` registers via `imageHistoryVM.Add(exposureData.MetaData.Image.Id, ImageType)`.
- A trigger only runs **between two items** (never during an `Execute`).
- Consequence: an atomic `TakeSerExposures` instruction **fires no trigger** (neither an
  IExposureItem nor a history entry) → only suitable for bursts without an intermediate trigger.

**Chosen architecture (2026-06-23, FINAL): `Capture SER Frame` replaces `Take Exposure`.**
The normal NINA flow stays **unchanged** (loop + conditions + native triggers). Only the capture
step is replaced. Three instructions:
- `StartSerRecording`: opens a `.ser` session (folder + current filter + split options). Placed
  before the loop.
- `CaptureSerFrame`: **LIGHT `IExposureItem`**, replacement for `Take Exposure`. Does
  `CaptureImage → ToImageData → SerRecorder.WriteFrame` (**no PrepareImage / star detection /
  FITS**), then `imageHistoryVM.Add(...)`. **Synchronous** write inside `Execute`.
- `StopSerRecording`: finalizes (trailer + FrameCount) and closes.
- Shared state: `SerRecorder` (static).

**Why (vs the rejected approaches):**
- Lucky-imaging performance: the `BeforeImageSaved` hook (intercepting NINA's save) imposed the heavy
  per-frame pipeline (prepare + star detection + FITS write). `Capture SER Frame` bypasses all of it.
- Triggers: being a LIGHT `IExposureItem` that registers in the history, native triggers (meridian
  flip, AF, dither "after N") count it and run **between frames** → the flip is handled **by NINA**,
  nothing to replicate.
- Synchronous write inside the item → **no race** on Stop (unlike the hook, where the asynchronous
  save of the last exposure arrived after Stop: lost 1 frame and left its FITS behind).

**Capture safety:**
- `SerWriter` is patched on **every frame** (`patchFrameCountEachFrame`) + flushed (Seek flushes) →
  the `.ser` is **valid at all times**; an abort / NINA crash / power cut leaves a readable file (at
  worst the last partial frame is ignored and the optional trailer is missing).
- `SerRecorder` finalizes any writer left open on the next `Start` (CloseDangling) **and** on the
  plugin `Teardown` (graceful NINA shutdown); access serialized by a `lock`.
- **Session bound to its sequence (2026-10-25)**: `Start` records the sequence root container
  (`ItemUtility.GetRootContainer(Parent)`) as owner. A frame coming from **another** sequence (a
  session left open by an abort) finalizes the old `.ser` and then fails ("No active SER
  recording") instead of being appended to it.
- **File naming**: an existing file is never overwritten (suffix `_2`, `_3`…); characters that are
  invalid in a file name are replaced by `_` in the filter name.
- `SerWriter.Dispose` releases the handle even if finalization fails (`try/finally`).
- `StartSerRecording.Validate` checks that the output folder is writable (fail fast before capture).

**Rejected:** (a) **FITS→SER hook** — FITS + prepare overhead per frame, no clean way to cancel the
FITS write; (b) **single `TakeSerExposures` block with inline AF/dither** — rewrote the trigger
logic and did not handle the meridian flip; removed.

**Known cosmetic effect:** since no FITS is written, NINA image-history thumbnails may be missing
(triggers only need the history counter).

## 7. Splitting the .ser (2026-10-25)

Stellar lucky-imaging sessions last hours and produce very large files (≈ 190 GB/hour for a 26 MP
sensor at 1 fps). Splitting is therefore **configurable on `Start SER Recording`**; the defaults
keep the single continuous file.
- **Max frames/file** (0 = no limit) and **Max size (GB, 1024³ bytes)** (0 = no limit), trailer
  included; a new file starts as soon as either limit would be exceeded. A file always gets at
  least one frame.
- **New file after meridian flip** (checkbox, off by default): `CaptureSerFrame` passes the mount's
  side of pier (`ITelescopeMediator.GetInfo().SideOfPier`); a change between two known values
  starts a new `.ser`, so post-flip frames (rotated 180°) are kept apart. An unknown side never
  splits.
- With the checkbox off, the previous behavior stays: post-flip frames rotated 180° in the **same**
  `.ser`, to be handled during processing (Siril star registration handles rotation, AutoStakkert
  does not).
- Rejected: hard-coded splitting (forces one policy on every setup); splitting by time (frame count
  and size map directly to stacking and disk constraints).
