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

**Architecture retenue (2026-06-23, finale) : un seul bloc `TakeSerExposures` avec AF/dither inline.**
Le montage en 3 items + containers a d'abord été livré, puis **abandonné** : l'utilisateur le trouvait
trop pénible (deux containers imbriqués à monter). Décision finale : tout dans **une seule instruction**.
- `TakeSerExposures` : boucle interne de `FrameCount` poses → **un seul .ser** ; champs `AutofocusEvery`
  et `DitherEvery`. Au milieu de la boucle (jamais après la dernière frame), si dû, on invoque les
  **vraies routines NINA** : `new RunAutofocus(...).Execute(...)` (autofocus) et `guiderMediator.Dither(token)`.
  Vérifié par décompilation que `RunAutofocus.Execute`/`Dither.Execute` ne dépendent pas du `Parent`,
  donc invocables en standalone. → AF s'exécute à la frame 1800 d'un 3600 et la capture reprend dans
  le même fichier. UI = `SequenceBlockView` (déjà maîtrisée, faible risque).
- **Robustesse abandon** : `SerWriter` patché à **chaque frame** (`patchFrameCountEachFrame`) → un SER
  abandonné reste valide (FrameCount correct, trailer optionnel manquant) ; finalisation aussi en `finally`.
- **Garde AF=0** : on n'appelle l'AF que si `AutofocusEvery > 0` (le trigger natif `AutofocusAfterExposures`
  ferait sinon un `% 0` → division par zéro).
- **Limite assumée** : AF/dither sont des champs, pas des triggers NINA ; les triggers du container parent
  (ex. meridian flip) ne peuvent pas s'intercaler pendant la boucle (un item ne peut être préempté).
  Si besoin de meridian flip mid-capture → revoir vers un container custom plus tard.
- Items `StartSerSession`/`CaptureSerFrame`/`FinalizeSerSession` + `SerSession` : **supprimés** (remplacés
  par le bloc unique). `CaptureSerFrame` était un `IExposureItem` qui appelait `imageHistoryVM.Add(...)` —
  motif conservé en référence dans MEMORY si on refait un container.
