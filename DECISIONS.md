# DECISIONS — Plugin NINA « SER Capture »

Ce fichier fige le périmètre, l'architecture et les choix déjà tranchés. Ne jamais
contredire une décision ici sans la flaguer d'abord. Le spec complet est
`nina-ser-capture-plan.md` ; ce document en consigne les décisions appliquées.

## 1. Périmètre

Plugin NINA (Nighttime Imaging 'N' Astronomy) ajoutant une instruction
« Take SER Exposures » à l'advanced sequencer : capture une rafale de frames brutes
dans **un seul fichier `.ser`** (lucky imaging), au lieu d'une pile de FITS. Les
loops/triggers/conditions/dither/AF du séquenceur opèrent **autour** de l'instruction.

## 2. Architecture

- **Une seule `SequenceItem`** (« Take SER Exposures »), pas de container (spec §Architecture).
- **Un SER = un événement séquenceur** (une exécution = une « pose » pour les triggers).
- Trois projets dans la solution :
  - `SerCapture.Ser` — lib autonome **sans dépendance NINA** (le `SerWriter` et le format).
    Testable hors NINA. C'est là qu'on dérisque le format binaire.
  - `SerCapture` — le plugin (WPF/MEF, référence NINA.Plugin). Référencera `SerCapture.Ser`
    en Phase 3 (capture).
  - `SerCapture.SerTest` — console de test du `SerWriter` (référence `SerCapture.Ser`).
- On gagne l'abstraction par l'usage : pas de HAL spéculative.

## 3. Phase 0 — from scratch (tranché)

Le plugin *Lucky Imaging* (Nick Holland) **n'est pas open source** : aucun repo public
trouvé (recherche web exhaustive le 2026-06-22), distribué en binaire via le manifest
NINA, et il n'est pas installé sur la machine. → On construit **from scratch**
(Phases 1→5 du spec), pas de fork.

## 4. Tooling & build

- **Scaffolding manuel via `dotnet`** (pas l'extension Visual Studio). `.csproj` SDK-style
  écrits à la main, build et déploiement en CLI.
- Référence : package NuGet **`NINA.Plugin` 3.2.0.9001** (méta-package qui tire
  transitivement Core/Sequencer/Image/WPF.Base/Plugin.Interfaces/Profile). Version alignée
  sur **NINA 3.2.0.9001 installé** (`C:\Program Files\N.I.N.A. ...`).
- Source d'API faisant autorité : packages NuGet + sources réelles du template officiel
  `isbeorn/nina.plugin.template`. **Aucune signature devinée.**
- Déploiement : copier la DLL dans `%LOCALAPPDATA%\NINA\Plugins\3.0.0\SerCapture\`
  (`3.0.0` = version de l'API plugin, pas de NINA ; confirmé via les plugins installés).

## 5. Format SER — décisions dures

- **Endianness (le « champ maudit », offset 22).** Le spec disait « flag = 1 » pour des
  données little-endian. **Décision : on écrit 0.** Vérifié dans la source de Siril
  (`src/io/ser.h`) : `SER_LITTLE_ENDIAN = 0`, `SER_BIG_ENDIAN = 1`, et `ser_create_file()`
  écrit `SER_LITTLE_ENDIAN` (= 0) pour des données little-endian. C'est le standard de fait
  (SharpCap/FireCapture/PIPP/AutoStakkert). On écrit donc les pixels little-endian (natif
  x86) **et** le champ offset 22 = **0**. → Contredit l'invariant #3 du spec, flaggé ici.
- **ColorID** (confirmés source Siril `ser.h`) : MONO=0, RGGB=8, GRBG=9, GBRG=10, BGGR=11.
- **PixelDepth (offset 34) = 16 en dur.** Les pixels de NINA (`IImageArray.FlatArray`) sont des
  `ushort` bruts *right-aligned* (un capteur 13/14 bits donne des valeurs 0..8191 / 0..16383, pas
  remises à l'échelle sur 16 bits). On déclare 16 (taille du conteneur, choix le plus répandu).
  Validé au simulateur (13 bits) : SER Player/AutoStakkert/PIPP auto-normalisent → image correcte.
  Limite connue : un viewer qui fait confiance au header sans auto-stretch affiche une image sombre.
  Option future si besoin : écrire la vraie profondeur (`IImageData.Properties.BitDepth`) à l'offset 34.
- **DateTime = `DateTime.Ticks` bruts** (100 ns depuis 0001-01-01), aucune conversion d'epoch.
  Offset 162 = local (`DateTime.Now.Ticks`), offset 170 = UTC (`DateTime.UtcNow.Ticks`).
- **FrameCount** écrit à 0 en placeholder (offset 38), patché à la fin (y compris sur annulation).
- **Trailer** : `FrameCount × 8` octets, un tick UTC int64 little-endian par frame.
- **Données RAW jamais débayerisées** ; streaming, jamais bufferiser la pile en RAM.
- **Nommage méthode** : la méthode de finalisation s'appelle `FinalizeFile()` et non
  `Finalize()` (réservé par le GC en C#, redéfinition interdite).

## 6. Triggers & flux « session » (redesign vs plan)

**Besoin réel utilisateur (2026-06-23)** : N poses unitaires (ex. 3600), **un seul fichier .ser**,
avec un trigger (AF/dither) qui s'exécute **au milieu** (ex. à 1800) puis reprise dans le même SER.
Cela **contredit** le plan (« une seule SequenceItem, pas de container » + « 1 SER = 1 événement »).
Flaggé et tranché avec l'utilisateur.

**Mécanique des triggers NINA (décompilée de 3.2.0.9001, certaine) :**
- `AutofocusAfterExposures.ShouldTrigger` / `DitherAfterExposures.ShouldTrigger` retournent `false`
  si `nextItem` n'est pas un **`IExposureItem` de type "LIGHT"**.
- Le compteur lit **`IImageHistoryVM.ImageHistory`** (LIGHT, `Id > lastAutoFocusId`).
- `TakeExposure` enregistre via `imageHistoryVM.Add(exposureData.MetaData.Image.Id, ImageType)`.
- Un trigger ne s'exécute **qu'entre deux items** (jamais pendant un `Execute`).
- Conséquence : l'instruction atomique `TakeSerExposures` (Phases 3-4) **ne déclenche aucun trigger**
  (ni IExposureItem, ni entrée d'historique) → réservée aux bursts sans trigger intermédiaire.

**Architecture retenue (2026-06-23, FINALE) : `Capture SER Frame` remplace `Take Exposure`.**
Le flux NINA normal reste **inchangé** (loop + conditions + triggers natifs). On ne remplace que l'étape
de capture. Trois instructions :
- `StartSerRecording` : ouvre une session `.ser` (dossier + filtre courant). À poser avant la boucle.
- `CaptureSerFrame` : **`IExposureItem` LIGHT**, remplaçant de `Take Exposure`. Fait
  `CaptureImage → ToImageData → SerRecorder.WriteFrame` (**aucun PrepareImage / star-detect / FITS**),
  puis `imageHistoryVM.Add(...)`. Écriture **synchrone** dans l'`Execute`.
- `StopSerRecording` : finalise (trailer + FrameCount) et ferme.
- État partagé : `SerRecorder` (statique).

**Pourquoi (vs les approches rejetées) :**
- Performance lucky : le hook `BeforeImageSaved` (intercepter la sauvegarde NINA) imposait le pipeline
  lourd par frame (prepare + star-detect + écriture FITS). `Capture SER Frame` court-circuite tout ça.
- Triggers : étant un `IExposureItem` LIGHT qui s'inscrit dans l'historique, les triggers natifs
  (meridian flip, AF, dither « after N ») le comptent et s'intercalent **entre les frames** → le flip
  est géré **par NINA**, rien à répliquer.
- Écriture synchrone dans l'item → **pas de race** au Stop (contrairement au hook, où la sauvegarde
  asynchrone de la dernière pose arrivait après le Stop : perdait 1 frame + laissait son FITS).

**Sécurité des captures :**
- `SerWriter` patché à **chaque frame** (`patchFrameCountEachFrame`) + flush (le Seek flushe) → le `.ser`
  est **valide à tout instant** ; un abandon / crash NINA / coupure laisse un fichier ouvrable (au pire
  la dernière frame partielle ignorée, trailer optionnel manquant).
- `SerRecorder` finalise un éventuel writer resté ouvert au prochain `Start` (CloseDangling) **et** au
  `Teardown` du plugin (fermeture NINA gracieuse) ; accès sérialisés par un `lock`.
- `StartSerRecording.Validate` vérifie que le dossier de sortie est inscriptible (fail-fast avant capture).

**Rejeté :** (a) **hook FITS→SER** — overhead FITS+prepare par frame (relevé par l'utilisateur), pas
d'annulation propre de l'écriture FITS ; (b) **bloc unique `TakeSerExposures` avec AF/dither inline** —
réécrivait la logique des triggers et ne gérait pas le meridian flip ; supprimé.

**Limite assumée :** un meridian flip au milieu = frames post-flip tournées 180° dans le **même** `.ser`
(choix utilisateur : un seul fichier continu) ; à gérer au traitement.

**Effet cosmétique connu :** comme aucun FITS n'est écrit, les vignettes de l'historique d'images NINA
peuvent être absentes (les triggers, eux, n'ont besoin que du compteur d'historique).
