# Civ ranking data

Builds `TadaPlay/Resources/civ-ranking-wk.json`, the data behind the "Xếp hạng phe" screen.

Inputs:

- `data.json` from https://aoe2-de-tools.herokuapp.com/data.json - Survivalist's
  "Simplified Tech Tree for 1v1 Arabia" grades (Definitive Edition balance).
- `Games/WK.xml` from the game folder - the 31 WololoKingdoms civs and their order.
- The civ descriptions from the game's own text: `Voobly Mods/AOC/Data Mods/v1.5 Game Data/language.ini`,
  string ids 20150-20180 (same order as WK.xml), extracted to a JSON list of `{name, desc}`.
- `civ_desc_vi.json` (here) - the Vietnamese translation of those descriptions, by civ key.

```
python make_civ_data.py data.json "<game>/Games/WK.xml" ../../TadaPlay/Resources/civ-ranking-wk.json wk_desc.json civ_desc_vi.json
```

Units that exist only in Definitive Edition (steppe lancer, ghulam, siege elephant) are dropped;
Indians use DE's Hindustanis grades.
