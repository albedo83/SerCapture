# Plan d'implémentation — Plugin NINA « SER Capture »

> **Objectif** : développer un plugin NINA qui remplace l'étape de prise de vue par une capture
> écrivant directement un fichier `.ser` (séquence d'images brutes type lucky imaging), tout en
> laissant le séquenceur avancé piloter normalement loops, triggers (autofocus, dither, meridian
> flip), conditions, switch filter et mount.

> **Pour Claude Code — règles de travail :**
> 1. Procéder **phase par phase**. Ne pas démarrer une phase tant que le *Definition of Done* de la
>    précédente n'est pas vert.
> 2. **Ne jamais inventer une signature d'API NINA.** Les noms exacts des méthodes des mediators
>    (`Capture`, `CaptureImage`, `Download`, `StartLiveView`…) doivent être vérifiés contre la source
>    NINA réelle. Utiliser le MCP **Context7** pour récupérer la doc/API NINA à jour, ou cloner le
>    repo NINA + le template et lire les interfaces. En cas de doute, lire le code, pas la mémoire.
> 3. Les **invariants & pièges** listés en fin de document sont des contraintes dures, pas des
>    suggestions. Tout SER produit doit s'ouvrir sans erreur dans **Siril** et **PIPP/AutoStakkert**.

---

## Phase 0 — Décision préalable (NE PAS SKIPPER)

Un plugin **Lucky Imaging** (Nick Hardy) existe déjà et fait l'essentiel du travail dur : container
dédié, instruction « Take Video Roi Exposures », ROI, mode vidéo natif de la caméra, bypass du
post-traitement par image (star detection / HFD / stats). Son **seul** manque : il sauve en FITS, pas
en SER (l'auteur l'avait annoncé puis jamais livré).

**Avant d'écrire une ligne**, déterminer la stratégie :

1. Chercher si Lucky Imaging est open source (repo GitHub/GitLab, Discord NINA `#plugin-discussions`).
2. **Si OUI** → le delta réel se réduit à « écrire un `SerWriter` au lieu d'une pile de FITS ».
   Forker, ou mieux contribuer le `SerWriter` upstream. On récupère gratis streaming vidéo, ROI et
   intégration séquenceur déjà rodée. → Sauter directement aux **Phases 2 + 4** appliquées au fork.
3. **Si NON / inaccessible** → construire from scratch en suivant les Phases 1 → 5 ci-dessous.

> Documenter la décision (et le lien du repo s'il existe) en tête du README du projet avant de coder.

---

## Stack & prérequis

- **C# / .NET 8**, WPF, MEF (Managed Extensibility Framework).
- Template officiel : `isbeorn/nina.plugin.template` (extension Visual Studio fournie dans les
  releases du repo).
- ⚠️ À la création du projet : **cocher « Place solution and project in the same directory »**, sinon
  les chemins des références NuGet sont cassés.
- ⚠️ L'assistant peut proposer .NET Framework 4.8 par erreur → **choisir .NET 8**.
- Doc plugins NINA : <https://nighttime-imaging.eu/docs/master/site/contributing/plugins/>

---

## Architecture cible

**On ne remplace pas le séquenceur, on remplace une brique.** Une instruction custom est un *pair* de
« Take Exposure » dans l'advanced sequencer. Les loops/triggers/conditions opèrent *autour* d'elle
sans connaître son contenu.

→ **Décision : une seule `SequenceItem` (« Take SER Exposures »), pas de container** (sauf si on
reprend la gestion ROI de Lucky, auquel cas un container devient pertinent — hors MVP).

**Point subtil à respecter (comptage des triggers).** Une exécution de l'instruction = **un** événement
pour les triggers, pas N. Chaque fichier SER doit donc être traité comme une « pose ». La structure de
séquence cible côté utilisateur :

```
Loop until 03:00
 ├─ Switch Filter (Ha)
 ├─ Take SER  (N frames × t sec)     ← notre instruction
 ├─ Dither
 └─ [trigger] Autofocus after 1 exposure
```

Chaque tour = un SER = un dither = éventuellement un AF. Dithérer *entre* frames d'un même SER n'a
aucun sens (on ne dithers pas pendant une vidéo) — ce découpage est donc le bon.

---

## Phase 1 — Bootstrap du plugin

**But :** faire apparaître une instruction vide « Take SER Exposures » dans l'advanced sequencer de
NINA.

- Générer le projet depuis le template VS.
- Créer la classe `TakeSerExposures : SequenceItem` avec les attributs MEF :
  ```csharp
  [ExportMetadata("Name", "Take SER Exposures")]
  [ExportMetadata("Description", "Capture a burst of raw frames into a single .ser file")]
  [ExportMetadata("Icon", "...")]            // réutiliser une icône NINA existante au début
  [ExportMetadata("Category", "Camera")]
  [Export(typeof(ISequenceItem))]
  public class TakeSerExposures : SequenceItem { ... }
  ```
- Implémenter a minima `Execute(...)` (stub), `Clone()`, ctor.
- Renseigner le manifest du plugin, builder, déposer le `.dll` dans le dossier plugins de NINA.

**Definition of Done :** NINA démarre, l'instruction apparaît dans la liste, peut être glissée dans
une séquence sans crash.

---

## Phase 2 — `SerWriter` (isolé, testable HORS NINA)

**But :** une classe autonome qui écrit un `.ser` correct. La tester via un petit programme console qui
génère un SER synthétique (ex. dégradé ou bruit) ouvrable dans Siril — **avant** de la brancher à NINA.
C'est ici qu'on dérisque tout le format.

### Layout du fichier
`Header (178 octets)` + `données brutes` + `trailer (optionnel)`.

| Offset | Champ          | Type   | Taille | Valeur                                            |
|-------:|----------------|--------|-------:|---------------------------------------------------|
| 0      | FileID         | string | 14     | `"LUCAM-RECORDER"`                                |
| 14     | LuID           | int32  | 4      | `0`                                               |
| 18     | ColorID        | int32  | 4      | MONO=0, RGGB=8, GRBG=9, GBRG=10, BGGR=11          |
| 22     | LittleEndian   | int32  | 4      | `1` pour données little-endian (**voir pièges**)  |
| 26     | ImageWidth     | int32  | 4      | largeur (px)                                      |
| 30     | ImageHeight    | int32  | 4      | hauteur (px)                                      |
| 34     | PixelDepth     | int32  | 4      | `16` (bits par plan)                              |
| 38     | FrameCount     | int32  | 4      | **placeholder, patché à la fin**                  |
| 42     | Observer       | string | 40     | depuis le profil                                  |
| 82     | Instrument     | string | 40     | `CameraInfo.Name`                                 |
| 122    | Telescope      | string | 40     | depuis le profil / mount                          |
| 162    | DateTime       | int64  | 8      | `DateTime.Now.Ticks` (local)                      |
| 170    | DateTime_UTC   | int64  | 8      | `DateTime.UtcNow.Ticks`                           |

- **Données** : `Width × Height × (PixelDepth/8) × FrameCount` octets, frames concaténées brutes.
- **Trailer** : `FrameCount × 8` octets, un `DateTime.UtcNow.Ticks` (UTC) par frame, dans l'ordre.

### API suggérée
```csharp
public sealed class SerWriter : IDisposable {
    public SerWriter(string path, int width, int height, SerColorId colorId, ...);
    public void WriteFrame(ReadOnlySpan<ushort> raw, DateTime utcTimestamp); // append + buffer ts
    public void Finalize();   // patch FrameCount @ offset 38, write trailer, flush
    public void Dispose();    // appelle Finalize() si pas déjà fait (sécurité annulation)
}
```

**Definition of Done :** un SER de N frames généré par le programme console s'ouvre dans Siril **et**
PIPP, FrameCount correct, dates plausibles, image non corrompue, histogramme non byte-swappé.

---

## Phase 3 — Capture (boucle de poses unitaires d'abord)

**But :** brancher `TakeSerExposures.Execute` sur la caméra via les mediators, chemin simple et
universel (marche y compris ASCOM). Optimisation vidéo → Phase 6.

- **Mediators injectés au ctor** (vérifier les interfaces exactes côté source NINA) :
  `ICameraMediator`, `IImagingMediator`, `IProfileService`, `IFilterWheelMediator`,
  `ITelescopeMediator`.
- Propriétés bindées : `ExposureTime`, `Gain`, `Offset`, `Binning`, `FrameCount`.
- Boucle dans `Execute` :
  ```csharp
  // pseudo — confirmer les vrais noms via Context7 / source NINA
  using var ser = new SerWriter(path, w, h, colorId, ...);
  for (int i = 0; i < FrameCount; i++) {
      token.ThrowIfCancellationRequested();
      var exposureData = await imagingMediator.CaptureImage(captureSeq, token, progress); // RAW, sans prepare/save
      var imageData    = await exposureData.ToImageData(progress, token);
      ser.WriteFrame(imageData.Data.FlatArray, DateTime.UtcNow);
      progress.Report(/* frame i+1 / FrameCount */);
  }
  ser.Finalize();
  ```
- **ColorID** dérivé du type de capteur de la caméra (`CameraInfo` / `SensorType`) : OSC Bayer →
  RGGB/GRBG/… selon pattern ; mono → MONO. **Écrire les données RAW, jamais débayerisées.**
- **Métadonnées SER** : `CameraInfo.Name` → Instrument ; filtre courant via
  `filterWheelMediator.GetInfo().SelectedFilter` → nom de fichier ; Observer/Telescope depuis
  `profileService.ActiveProfile`.
- **Annulation** : sur `OperationCanceledException`, finaliser le SER avec les frames déjà capturées
  (patch FrameCount) — pas de fichier corrompu.
- **Nommage** : pattern propre exposé en propriété (`$$TARGETNAME$$_$$FILTER$$_$$DATETIME$$.ser`) OU
  réutiliser `profileService.ActiveProfile.ImageFileSettings`.

**Definition of Done :** une instruction seule, caméra connectée, produit un `.ser` multi-frames
valide (ouvert dans Siril) avec gain/offset/expo respectés et métadonnées correctes.

---

## Phase 4 — UI (DataTemplate)

**But :** champs de saisie dans le bloc de l'instruction.

- `ResourceDictionary` exporté via `[Export(typeof(ResourceDictionary))]` dans le code-behind.
- `DataTemplate` ciblant `TakeSerExposures`, basé sur `nina:SequenceBlockView` →
  `SequenceBlockView.SequenceItemContent` (gère la mise en page standard).
- Contrôles : Exposure, Gain, Offset, Binning, Frame count, output pattern.
- Implémenter `Validate()` : caméra connectée, FrameCount > 0, chemin de sortie inscriptible,
  espace disque suffisant (cf. taille estimée ci-dessous).

**Definition of Done :** réglages éditables dans l'UI, persistés au save/load de la séquence
(round-trip JSON), validations affichées.

---

## Phase 5 — Intégration séquenceur & validation terrain

**But :** prouver que le séquenceur pilote bien autour de l'instruction.

- Construire une vraie séquence : `Loop until time` → `Switch Filter` → `Take SER` → `Dither` →
  trigger `Autofocus after N exposures`.
- Vérifier : chaque tour produit **un** SER, le dither s'exécute **entre** les SER, l'AF se déclenche
  au bon compte (un SER = une « exposure »).
- Tester sur les deux caméras cibles : **ASI294MC Pro** (OSC, ColorID RGGB) et **ToupTek G3M2210M**
  (mono, ColorID MONO).

**Definition of Done :** nuit de test (ou simulateur caméra NINA) sans crash, fichiers exploitables en
stacking, triggers correctement comptés.

---

## Phase 6 (optionnelle) — Mode vidéo natif (haut débit)

À n'aborder que si le débit de la Phase 3 est insuffisant (planétaire, poses < ~500 ms). La boucle de
poses unitaires plafonne à ~50–82 % d'efficacité à cause de l'overhead download/USB par frame.

- Utiliser `cameraMediator.StartLiveView()` / `DownloadLiveView()` (noms à confirmer).
- **Caveats à valider driver par driver :** le live view de NINA n'est pas garanti full-bit-depth
  (certains drivers crachent du 8 bits) ; l'ASCOM n'a quasi jamais de mode vidéo. → marche surtout sur
  drivers natifs ZWO/QHY/ToupTek.
- **Ne pas** ouvrir le SDK caméra en direct en parallèle de NINA : contention du handle USB = enfer.

---

## Invariants & pièges (contraintes dures — À RESPECTER)

1. **Ticks = format natif SER.** Les DateTime SER sont des ticks Windows (100 ns depuis 0001-01-01) =
   exactement `DateTime.Ticks` en .NET. **Aucune conversion d'epoch manuelle** (sinon dates absurdes
   type année 0119/0230, bug classique).
2. **FrameCount patché à la fin.** Écrire 0 en placeholder (offset 38), streamer, puis `Seek(38)` et
   écrire le compte réel. Oubli = fichier corrompu de 2 Go (bug INDI connu). Patcher aussi sur
   annulation.
3. **Endianness, le champ maudit.** Un `ushort[]` x86 écrit brut est little-endian → flag `1`. MAIS
   des writers historiques mentent et certains readers ignorent/inversent le flag. → écrire
   little-endian, flag = 1, **et vérifier dans PIPP/AutoStakkert/Siril** que l'histogramme n'est pas
   absurde (octets swappés = bruit pur).
4. **Données RAW, jamais débayerisées.** Le post-traitement NINA (debayer, stretch, star detect) doit
   être contourné. ColorID encode le pattern Bayer ; le stacker débayerisera.
5. **Mémoire : streamer, jamais bufferiser la pile.** Une frame ASI294 ≈ 23 Mo → 1000 frames ≈ 23 Go.
   `FileStream` + gros buffer ; n'accumuler en RAM que la liste des timestamps (`List<long>`).
6. **Un SER = un événement séquenceur.** Ne pas reporter chaque frame comme une exposure, sinon les
   triggers (AF/dither après N) comptent les frames au lieu des SER.
7. **Estimation taille disque** (pour `Validate`) : `Width × Height × 2 × FrameCount` + 178 +
   `8 × FrameCount`.

---

## Définition of Done globale

- [ ] Phase 0 documentée (fork Lucky vs from-scratch tranché).
- [ ] SER produits ouvrables dans **Siril** ET **PIPP/AutoStakkert**, sur OSC (RGGB) et mono.
- [ ] Gain/offset/exposition/binning respectés ; métadonnées (camera, telescope, observer, dates)
      correctes.
- [ ] Annulation à mi-capture → SER finalisé propre.
- [ ] Intégration séquenceur validée (loop / filter / dither / autofocus comptés correctement).
- [ ] README : installation, structure de séquence recommandée, limites (débit, live view).

---

## Ressources

- Template plugin : <https://github.com/isbeorn/nina.plugin.template>
- Doc plugins NINA : <https://nighttime-imaging.eu/docs/master/site/contributing/plugins/>
- Spéc SER (PDF de référence, Grischa Hahn) : `SER Doc V2.pdf`
- Doc SER côté Siril : <https://siril.readthedocs.io/en/latest/file-formats/SER.html>
- Discord NINA : canal `#plugin-discussions` (pour statut OSS de Lucky Imaging)
- **Context7 MCP** : récupérer les interfaces NINA exactes (mediators, `SequenceItem`,
  `IImageData`) plutôt que de deviner.
