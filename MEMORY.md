# MEMORY — journal des décisions

Format : `[Date], [Décision]` / Ce qui a été décidé / Pourquoi / Ce qui a été rejeté.

---

**2026-06-22, Phase 0 — from scratch plutôt que fork de Lucky Imaging**
- Décidé : construire le plugin depuis zéro (Phases 1→5 du spec).
- Pourquoi : le plugin *Lucky Imaging* de Nick Holland n'est pas open source (recherche web
  exhaustive : aucun repo public, distribution binaire via manifest NINA, non installé ici).
- Rejeté : forker/contribuer un `SerWriter` upstream — impossible sans accès au code source.

**2026-06-22, Scaffolding manuel via dotnet (pas l'extension VS)**
- Décidé : `.csproj` SDK-style écrits à la main, build/déploiement en CLI.
- Pourquoi : le template officiel n'existe que comme extension Visual Studio (pas de
  `dotnet new`) ; on travaille en CLI. Un plugin NINA n'est qu'une lib WPF .NET 8 + exports MEF.
- Rejeté : passer par l'extension VS (action manuelle, hors flux CLI).

**2026-06-22, Référence NuGet NINA.Plugin 3.2.0.9001**
- Décidé : référencer le seul méta-package `NINA.Plugin` en 3.2.0.9001.
- Pourquoi : aligné sur NINA 3.2.0.9001 installé ; tire transitivement Core/Sequencer/
  Image/WPF.Base. Le compilateur impose alors les bonnes signatures (zéro API devinée).
- Rejeté : lister 8 packages NINA.* séparément (inutile, redondant).

**2026-06-22, Champ SER « LittleEndian » (offset 22) = 0 pour données little-endian**
- Décidé : écrire les pixels little-endian (natif x86) ET le champ offset 22 = 0.
- Pourquoi : la source de Siril (`src/io/ser.h`) définit `SER_LITTLE_ENDIAN = 0`,
  `SER_BIG_ENDIAN = 1`, et écrit 0 pour des données LE. Standard de fait des writers.
- Rejeté : « flag = 1 » comme l'indiquait l'invariant #3 du spec — contredit la source de
  Siril (cible de validation). Discrépance flaggée dans DECISIONS.md §5.

**2026-06-23, Phases 3 & 4 validées au simulateur NINA**
- Décidé : capture + UI considérées OK.
- Pourquoi : un `.ser` 640×480 / 20 frames produit par le simulateur s'ouvre dans SER Player,
  header correct (LittleEndian=0, FrameCount=20), filesize exact, timestamps ordonnés, nom de
  filtre repris, champs UI éditables. Le « PixelDepth 13 » de SER Player = bits significatifs
  détectés (capteur sim 13 bits), header bien à 16 (octets vérifiés). Cf. DECISIONS.md §5.

**2026-06-23, Bloc unique avec AF/dither inline (décision finale)**
- Décidé : tout dans `TakeSerExposures` (boucle interne → 1 seul .ser) avec champs `AutofocusEvery` /
  `DitherEvery` qui invoquent `RunAutofocus.Execute` et `guiderMediator.Dither(token)` au milieu de la boucle.
- Pourquoi : besoin réel = N poses unitaires dans UN seul .ser avec AF/dither au milieu (ex. 1800/3600)
  puis reprise. Le bloc unique = aucun montage (pas de Loop ni container), UI `SequenceBlockView` maîtrisée.
  `RunAutofocus`/`Dither` ne dépendent pas du `Parent` (décompilé NINA 3.2.0.9001) → invocables standalone.
- Rejeté : (a) **flux 3 items** (StartSerSession/CaptureSerFrame/FinalizeSerSession) — livré puis supprimé,
  l'utilisateur le trouvait trop pénible (deux containers imbriqués à monter) ; (b) **container custom 1-bloc**
  — UI hiérarchique (`HierarchicalSequenceContainerView`) trop risquée pour la v1 ; (c) embarquer le trigger
  natif `AutofocusAfterExposures` dans un container immuable — `% AfterExposures` plante si 0.
- Réf. pour un futur container : `CaptureSerFrame` doit être `IExposureItem` LIGHT + appeler
  `imageHistoryVM.Add(exposureData.MetaData.Image.Id, "LIGHT")` pour que les triggers natifs comptent.
- Limite assumée : AF/dither = champs, pas triggers NINA ; meridian flip mid-capture impossible (un item
  ne se préempte pas). Cf. DECISIONS.md §6.

**2026-06-22, Méthode de finalisation nommée `FinalizeFile()`**
- Décidé : `FinalizeFile()` au lieu de `Finalize()` proposé par le spec.
- Pourquoi : `Finalize()` est le finaliseur réservé par le GC en C# — on ne peut pas le
  redéfinir comme méthode publique. `Dispose()` appelle `FinalizeFile()` si non déjà fait.
