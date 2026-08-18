# Third-party models

Everything here is **CC0 1.0 Universal** (public domain dedication). No
attribution is legally required; it is recorded anyway so provenance is not lost,
and so anyone auditing the repository can confirm redistribution is permitted.

| Folder | Source | Author | Licence | Retrieved |
| --- | --- | --- | --- | --- |
| `Nature/` | [Nature Kit](https://kenney.nl/assets/nature-kit) | Kenney | CC0 1.0 | 2026-08-18 |
| `Cars/` | [Car Kit](https://kenney.nl/assets/car-kit) | Kenney | CC0 1.0 | 2026-08-18 |
| `City/` | [City Kit (Commercial)](https://kenney.nl/assets/city-kit-commercial) | Kenney | CC0 1.0 | 2026-08-18 |
| `Dinosaurs/` | [Animated Dinosaur Pack](https://quaternius.com/packs/animateddinosaurs.html) | Quaternius | CC0 1.0 | 2026-08-18 |

Licence text: https://creativecommons.org/publicdomain/zero/1.0/

## What was taken

Only the FBX models actually used are committed, not the full packs — the Nature
Kit alone ships 329 models across five formats plus 1,600 isometric sprites, and
none of that belongs in this repository. The original archives are downloaded to
`thirdparty/`, which is git-ignored.

- **Nature** — 9 trees, 5 rocks, 1 stump
- **Cars** — 8 vehicles
- **City** — 6 buildings
- **Dinosaurs** — Trex, Triceratops, Stegosaurus, Apatosaurus, Parasaurolophus,
  Velociraptor

## Notes

Neither Kenney kit ships texture files for these particular models; colour comes
from materials embedded in the FBX, so there is nothing else to copy alongside
them.

Models are **not** scaled at import. `PropScatter` normalises each one by its
measured bounds to a per-kind `TargetHeight` in metres, because an imported model
arrives in whatever units its author chose — Kenney kits are roughly one unit per
tile, not per metre. Measuring beats hardcoding a per-pack scale factor that
silently breaks the moment a model is swapped.

The Quaternius dinosaurs are **rigged and animated**, but nothing currently plays
those animations; they stand as static models. Driving them is a separate piece
of work.
