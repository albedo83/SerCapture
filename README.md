# SER Capture — plugin NINA

Plugin pour [NINA](https://nighttime-imaging.eu/) ajoutant une instruction
**« Take SER Exposures »** à l'advanced sequencer : capture une rafale de frames brutes
dans un seul fichier `.ser` (lucky imaging) plutôt qu'une pile de FITS. Le séquenceur
continue de piloter loops, filtres, dither, autofocus et meridian flip autour de l'instruction.

## Phase 0 — décision

Le plugin *Lucky Imaging* (Nick Holland) couvrait déjà l'essentiel mais ne sauve qu'en FITS
et **n'est pas open source** (aucun repo public, distribution binaire via le manifest NINA).
Ce projet est donc construit **from scratch**. Voir `DECISIONS.md` et `nina-ser-capture-plan.md`.

## État d'avancement

- [x] **Phase 1** — squelette du plugin (instruction « Take SER Exposures » visible dans NINA).
- [x] **Phase 2** — `SerWriter` isolé + console de test (`.ser` valide, vérifié structurellement).
- [ ] Phase 3 — capture réelle via les mediators NINA.
- [ ] Phase 4 — UI (DataTemplate) + `Validate()`.
- [ ] Phase 5 — intégration séquenceur & validation terrain.

## Structure

```
SerCapture.Ser/       lib autonome (format SER, SerWriter) — aucune dépendance NINA
SerCapture/           le plugin NINA (WPF/MEF, NINA.Plugin 3.2.0.9001)
SerCapture.SerTest/   console de test du SerWriter
```

## Prérequis

- .NET 8 SDK (+ workload Windows Desktop / WPF).
- NINA 3.2.x installé (référence NuGet `NINA.Plugin` 3.2.0.9001).

## Build

```powershell
dotnet build -c Release SerCapture.sln
```

## Tester le SerWriter (hors NINA)

```powershell
dotnet run -c Release --project SerCapture.SerTest
```

Génère `test_mono.ser` et `test_rggb.ser`, vérifie la structure (taille, FrameCount, ticks,
flag d'endianness) et affiche PASS/FAIL. **Vérification manuelle** : ouvrir les deux fichiers
dans **Siril** et **PIPP/AutoStakkert** — l'image doit être correcte et l'histogramme sain
(des octets byte-swappés donneraient du bruit pur).

## Déployer le plugin dans NINA

Après build, copier la DLL dans le dossier des plugins :

```powershell
$dst = "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\SerCapture"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "SerCapture\bin\Release\net8.0-windows\SerCapture.dll" $dst -Force
```

Relancer NINA : l'instruction « Take SER Exposures » apparaît dans la catégorie *Camera*
de l'advanced sequencer.

## Structure de séquence recommandée

```
Loop until 03:00
 ├─ Switch Filter (Ha)
 ├─ Take SER  (N frames × t sec)     ← cette instruction
 ├─ Dither
 └─ [trigger] Autofocus after 1 exposure
```

Chaque tour = un SER = un dither = éventuellement un AF (un SER compte pour une « pose »).
