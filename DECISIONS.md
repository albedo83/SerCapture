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
