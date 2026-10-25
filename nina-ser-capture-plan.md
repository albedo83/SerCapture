# Implementation plan — NINA plugin "SER Capture"

> **Note:** this is the original specification. Where the implementation deliberately deviates from
> it (endianness flag, trigger model, Start/Capture/Stop flow), the deviation is recorded and
> justified in `DECISIONS.md`, which takes precedence.

> **Goal**: build a NINA plugin that replaces the capture step with a capture writing directly to a
> `.ser` file (a sequence of raw frames, lucky-imaging style), while letting the advanced sequencer
> drive loops, triggers (autofocus, dither, meridian flip), conditions, filter switching and the mount
> as usual.

> **Working rules:**
> 1. Proceed **phase by phase**. Do not start a phase until the *Definition of Done* of the previous
>    one is green.
> 2. **Never invent a NINA API signature.** The exact names of the mediator methods (`Capture`,
>    `CaptureImage`, `Download`, `StartLiveView`…) must be checked against the real NINA source:
>    clone the NINA repo and the plugin template and read the interfaces, or inspect the NuGet
>    packages. When in doubt, read the code.
> 3. The **invariants & pitfalls** listed at the end of this document are hard constraints, not
>    suggestions. Every SER produced must open without error in **Siril** and **PIPP/AutoStakkert**.

---

## Phase 0 — Preliminary decision (DO NOT SKIP)

A **Lucky Imaging** plugin (Nick Hardy) already exists and does most of the hard work: a dedicated
container, a "Take Video Roi Exposures" instruction, ROI, the camera's native video mode, and bypassing
per-image post-processing (star detection / HFD / stats). Its **only** gap: it saves FITS, not SER
(the author announced SER support but never shipped it).

**Before writing a line of code**, settle the strategy:

1. Find out whether Lucky Imaging is open source (GitHub/GitLab repo, NINA Discord
   `#plugin-discussions`).
2. **If YES** → the real delta shrinks to "write a `SerWriter` instead of a stack of FITS". Fork it,
   or better, contribute the `SerWriter` upstream. Video streaming, ROI and a proven sequencer
   integration come for free. → Jump straight to **Phases 2 + 4** applied to the fork.
3. **If NO / not accessible** → build from scratch following Phases 1 → 5 below.

> Record the decision (and the repo link if any) at the top of the project README before coding.

---

## Stack & prerequisites

- **C# / .NET 8**, WPF, MEF (Managed Extensibility Framework).
- Official template: `isbeorn/nina.plugin.template` (Visual Studio extension provided in the repo
  releases).
- ⚠️ When creating the project: **tick "Place solution and project in the same directory"**,
  otherwise the NuGet reference paths are broken.
- ⚠️ The wizard may wrongly suggest .NET Framework 4.8 → **choose .NET 8**.
- NINA plugin docs: <https://nighttime-imaging.eu/docs/master/site/contributing/plugins/>

---

## Target architecture

**We do not replace the sequencer, we replace one building block.** A custom instruction is a *peer*
of "Take Exposure" in the advanced sequencer. Loops/triggers/conditions operate *around* it without
knowing what it contains.

→ **Decision: a single `SequenceItem` ("Take SER Exposures"), no container** (unless we take over
Lucky's ROI handling, in which case a container becomes relevant — out of MVP scope).

**Subtle point to respect (trigger counting).** One execution of the instruction = **one** event for
the triggers, not N. Each SER file must therefore be treated as one "exposure". The target sequence
structure on the user side:

```
Loop until 03:00
 ├─ Switch Filter (Ha)
 ├─ Take SER  (N frames × t sec)     ← our instruction
 ├─ Dither
 └─ [trigger] Autofocus after 1 exposure
```

Each iteration = one SER = one dither = possibly one AF. Dithering *between* frames of the same SER
makes no sense (you do not dither during a video) — so this split is the right one.

---

## Phase 1 — Plugin bootstrap

**Goal:** make an empty "Take SER Exposures" instruction appear in NINA's advanced sequencer.

- Generate the project from the VS template.
- Create the class `TakeSerExposures : SequenceItem` with the MEF attributes:
  ```csharp
  [ExportMetadata("Name", "Take SER Exposures")]
  [ExportMetadata("Description", "Capture a burst of raw frames into a single .ser file")]
  [ExportMetadata("Icon", "...")]            // reuse an existing NINA icon at first
  [ExportMetadata("Category", "Camera")]
  [Export(typeof(ISequenceItem))]
  public class TakeSerExposures : SequenceItem { ... }
  ```
- Implement at least `Execute(...)` (stub), `Clone()`, constructor.
- Fill in the plugin manifest, build, drop the `.dll` into NINA's plugins folder.

**Definition of Done:** NINA starts, the instruction appears in the list and can be dragged into a
sequence without crashing.

---

## Phase 2 — `SerWriter` (isolated, testable OUTSIDE NINA)

**Goal:** a standalone class that writes a correct `.ser`. Test it with a small console program that
generates a synthetic SER (e.g. gradient or noise) that opens in Siril — **before** wiring it into
NINA. This is where the whole format gets de-risked.

### File layout
`Header (178 bytes)` + `raw data` + `trailer (optional)`.

| Offset | Field          | Type   | Size | Value                                             |
|-------:|----------------|--------|-----:|---------------------------------------------------|
| 0      | FileID         | string | 14   | `"LUCAM-RECORDER"`                                |
| 14     | LuID           | int32  | 4    | `0`                                               |
| 18     | ColorID        | int32  | 4    | MONO=0, RGGB=8, GRBG=9, GBRG=10, BGGR=11          |
| 22     | LittleEndian   | int32  | 4    | `1` for little-endian data (**see pitfalls**)     |
| 26     | ImageWidth     | int32  | 4    | width (px)                                        |
| 30     | ImageHeight    | int32  | 4    | height (px)                                       |
| 34     | PixelDepth     | int32  | 4    | `16` (bits per plane)                             |
| 38     | FrameCount     | int32  | 4    | **placeholder, patched at the end**               |
| 42     | Observer       | string | 40   | from the profile                                  |
| 82     | Instrument     | string | 40   | `CameraInfo.Name`                                 |
| 122    | Telescope      | string | 40   | from the profile / mount                          |
| 162    | DateTime       | int64  | 8    | `DateTime.Now.Ticks` (local)                      |
| 170    | DateTime_UTC   | int64  | 8    | `DateTime.UtcNow.Ticks`                           |

- **Data**: `Width × Height × (PixelDepth/8) × FrameCount` bytes, raw frames concatenated.
- **Trailer**: `FrameCount × 8` bytes, one `DateTime.UtcNow.Ticks` (UTC) per frame, in order.

### Suggested API
```csharp
public sealed class SerWriter : IDisposable {
    public SerWriter(string path, int width, int height, SerColorId colorId, ...);
    public void WriteFrame(ReadOnlySpan<ushort> raw, DateTime utcTimestamp); // append + buffer ts
    public void Finalize();   // patch FrameCount @ offset 38, write trailer, flush
    public void Dispose();    // calls Finalize() if not done yet (cancellation safety)
}
```

**Definition of Done:** an N-frame SER generated by the console program opens in Siril **and** PIPP,
correct FrameCount, plausible dates, uncorrupted image, histogram not byte-swapped.

---

## Phase 3 — Capture (single-exposure loop first)

**Goal:** wire `TakeSerExposures.Execute` to the camera through the mediators, using the simple and
universal path (works with ASCOM too). Video optimization → Phase 6.

- **Mediators injected in the constructor** (check the exact interfaces in the NINA source):
  `ICameraMediator`, `IImagingMediator`, `IProfileService`, `IFilterWheelMediator`,
  `ITelescopeMediator`.
- Bound properties: `ExposureTime`, `Gain`, `Offset`, `Binning`, `FrameCount`.
- Loop in `Execute`:
  ```csharp
  // pseudo — confirm the real names against the NINA source
  using var ser = new SerWriter(path, w, h, colorId, ...);
  for (int i = 0; i < FrameCount; i++) {
      token.ThrowIfCancellationRequested();
      var exposureData = await imagingMediator.CaptureImage(captureSeq, token, progress); // RAW, no prepare/save
      var imageData    = await exposureData.ToImageData(progress, token);
      ser.WriteFrame(imageData.Data.FlatArray, DateTime.UtcNow);
      progress.Report(/* frame i+1 / FrameCount */);
  }
  ser.Finalize();
  ```
- **ColorID** derived from the camera sensor type (`CameraInfo` / `SensorType`): OSC Bayer →
  RGGB/GRBG/… depending on the pattern; mono → MONO. **Write RAW data, never debayered.**
- **SER metadata**: `CameraInfo.Name` → Instrument; current filter via
  `filterWheelMediator.GetInfo().SelectedFilter` → file name; Observer/Telescope from
  `profileService.ActiveProfile`.
- **Cancellation**: on `OperationCanceledException`, finalize the SER with the frames already
  captured (patch FrameCount) — no corrupted file.
- **Naming**: a clean pattern exposed as a property (`$$TARGETNAME$$_$$FILTER$$_$$DATETIME$$.ser`) OR
  reuse `profileService.ActiveProfile.ImageFileSettings`.

**Definition of Done:** a single instruction, camera connected, produces a valid multi-frame `.ser`
(opened in Siril) with gain/offset/exposure honored and correct metadata.

---

## Phase 4 — UI (DataTemplate)

**Goal:** input fields in the instruction block.

- `ResourceDictionary` exported via `[Export(typeof(ResourceDictionary))]` in the code-behind.
- `DataTemplate` targeting `TakeSerExposures`, based on `nina:SequenceBlockView` →
  `SequenceBlockView.SequenceItemContent` (handles the standard layout).
- Controls: Exposure, Gain, Offset, Binning, Frame count, output pattern.
- Implement `Validate()`: camera connected, FrameCount > 0, output path writable, enough disk space
  (see the size estimate below).

**Definition of Done:** settings editable in the UI, persisted on sequence save/load (JSON round
trip), validation messages displayed.

---

## Phase 5 — Sequencer integration & field validation

**Goal:** prove that the sequencer drives everything correctly around the instruction.

- Build a real sequence: `Loop until time` → `Switch Filter` → `Take SER` → `Dither` →
  trigger `Autofocus after N exposures`.
- Check: each iteration produces **one** SER, the dither runs **between** SERs, the AF fires at the
  right count (one SER = one "exposure").
- Test on both target cameras: **ASI294MC Pro** (OSC, ColorID RGGB) and **ToupTek G3M2210M**
  (mono, ColorID MONO).

**Definition of Done:** a test night (or the NINA camera simulator) without crashes, files usable for
stacking, triggers counted correctly.

---

## Phase 6 (optional) — Native video mode (high throughput)

Only tackle this if Phase 3 throughput is not enough (planetary, exposures < ~500 ms). The
single-exposure loop caps at ~50–82 % efficiency because of the per-frame download/USB overhead.

- Use `cameraMediator.StartLiveView()` / `DownloadLiveView()` (names to be confirmed).
- **Caveats to validate driver by driver:** NINA's live view is not guaranteed to be full bit depth
  (some drivers output 8 bits); ASCOM almost never has a video mode. → works mostly with native
  ZWO/QHY/ToupTek drivers.
- **Do not** open the camera SDK directly alongside NINA: USB handle contention is a nightmare.

---

## Invariants & pitfalls (hard constraints — MUST BE RESPECTED)

1. **Ticks = native SER format.** SER DateTimes are Windows ticks (100 ns since 0001-01-01) =
   exactly `DateTime.Ticks` in .NET. **No manual epoch conversion** (otherwise absurd dates such as
   year 0119/0230, a classic bug).
2. **FrameCount patched at the end.** Write 0 as a placeholder (offset 38), stream, then `Seek(38)`
   and write the real count. Forgetting it = a corrupted 2 GB file (known INDI bug). Patch on
   cancellation too.
3. **Endianness, the cursed field.** A raw x86 `ushort[]` is little-endian → flag `1`. BUT some
   historical writers lie and some readers ignore/invert the flag. → write little-endian, flag = 1,
   **and check in PIPP/AutoStakkert/Siril** that the histogram is sane (swapped bytes = pure noise).
4. **RAW data, never debayered.** NINA's post-processing (debayer, stretch, star detection) must be
   bypassed. ColorID encodes the Bayer pattern; the stacker will debayer.
5. **Memory: stream, never buffer the stack.** One ASI294 frame ≈ 23 MB → 1000 frames ≈ 23 GB.
   `FileStream` + a large buffer; only keep the timestamp list (`List<long>`) in RAM.
6. **One SER = one sequencer event.** Do not report each frame as an exposure, otherwise the triggers
   (AF/dither after N) count frames instead of SERs.
7. **Disk size estimate** (for `Validate`): `Width × Height × 2 × FrameCount` + 178 +
   `8 × FrameCount`.

---

## Overall Definition of Done

- [ ] Phase 0 documented (Lucky fork vs from scratch settled).
- [ ] SERs open in **Siril** AND **PIPP/AutoStakkert**, on OSC (RGGB) and mono.
- [ ] Gain/offset/exposure/binning honored; metadata (camera, telescope, observer, dates) correct.
- [ ] Cancellation mid-capture → cleanly finalized SER.
- [ ] Sequencer integration validated (loop / filter / dither / autofocus counted correctly).
- [ ] README: installation, recommended sequence structure, limitations (throughput, live view).

---

## Resources

- Plugin template: <https://github.com/isbeorn/nina.plugin.template>
- NINA plugin docs: <https://nighttime-imaging.eu/docs/master/site/contributing/plugins/>
- SER specification (reference PDF, Grischa Hahn): `SER Doc V2.pdf`
- SER docs on the Siril side: <https://siril.readthedocs.io/en/latest/file-formats/SER.html>
- NINA Discord: `#plugin-discussions` channel (for the open-source status of Lucky Imaging)
