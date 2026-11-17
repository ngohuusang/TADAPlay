"""Builds TadaPlay/Resources/civ-ranking-wk.json from the Simplified Tech Tree data.json.

Keeps only the 31 civilizations WololoKingdoms (UserPatch 1.5) ships, in the order the game's
Games/WK.xml lists them, and drops units that exist only in Definitive Edition.
"""
import json
import sys

src, wk_xml, out, desc_en, desc_vi = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
# Descriptions: the game's own text (language.ini 20150+, same order as WK.xml) and its
# Vietnamese translation, keyed by WK civ key.
en_list = json.load(open(desc_en, encoding='utf-8'))
vi_desc = json.load(open(desc_vi, encoding='utf-8'))
d = json.load(open(src, encoding='utf-8'))
civs = {c['name']: c for c in d['civilizations']}

# WK.xml civ key -> (DE name in data.json, Vietnamese name)
WK = {
    'briton': ('Britons', 'Anh'), 'frankish': ('Franks', 'Frank'), 'gothic': ('Goths', 'Goth'),
    'teutonic': ('Teutons', 'Teuton'), 'japanese': ('Japanese', 'Nhật Bản'),
    'chinese': ('Chinese', 'Trung Quốc'), 'byzantine': ('Byzantines', 'Byzantine'),
    'persian': ('Persians', 'Ba Tư'), 'saracen': ('Saracens', 'Saracen'),
    'turkish': ('Turks', 'Thổ Nhĩ Kỳ'), 'viking': ('Vikings', 'Viking'),
    'mongol': ('Mongols', 'Mông Cổ'), 'celtic': ('Celts', 'Celt'),
    'spanish': ('Spanish', 'Tây Ban Nha'), 'aztec': ('Aztecs', 'Aztec'), 'mayan': ('Mayans', 'Maya'),
    'hun': ('Huns', 'Hung Nô'), 'korean': ('Koreans', 'Triều Tiên'), 'italian': ('Italians', 'Ý'),
    'indian': ('Hindustanis', 'Ấn Độ'), 'incan': ('Incas', 'Inca'), 'magyar': ('Magyars', 'Magyar'),
    'slavic': ('Slavs', 'Slav'), 'portuguese': ('Portuguese', 'Bồ Đào Nha'),
    'ethiopian': ('Ethiopians', 'Ethiopia'), 'malian': ('Malians', 'Mali'),
    'berbers': ('Berbers', 'Berber'), 'khmer': ('Khmer', 'Khmer'), 'malay': ('Malay', 'Mã Lai'),
    'burmese': ('Burmese', 'Miến Điện'), 'vietnamese': ('Vietnamese', 'Việt Nam'),
}
# The English name the WK game itself shows (DE renamed Indians to Hindustanis).
GAME_NAME = {'indian': 'Indians'}

# Units graded on the site that WololoKingdoms does not have at all.
# (Imperial Skirmisher is NOT among them: in 1.5 it is the Vietnamese team bonus.)
DE_ONLY = {'elite steppe lancer', 'elite ghulam', 'siege elephant'}

NOTES = {
    'indian': 'Dữ liệu lấy từ phe Hindustanis của bản DE (thay cho Indians). Lính đặc biệt '
              'của Ấn Độ trong 1.5 là Elephant Archer - không có điểm trên trang gốc.',
    'mongol': 'Steppe Lancer chỉ có ở bản DE nên đã bỏ.',
}

# Keep the game's own order of civs from WK.xml (skip gaia).
import re
order = [m for m in re.findall(r'name="([a-z]+)"', open(wk_xml, encoding='utf-8-sig').read())
         if m != 'gaia']
assert set(order) == set(WK), set(order) ^ set(WK)

assert len(en_list) == len(order)
result = []
for i, key in enumerate(order):
    de, vi = WK[key]
    c = civs[de]
    ranks = {}
    for building, units in c['ranks'].items():
        kept = {u: v for u, v in units.items() if u not in DE_ONLY}
        if kept:
            ranks[building] = kept
    result.append({
        'key': key,
        'name': GAME_NAME.get(key, de),
        'vi': vi,
        'source': de,
        'note': NOTES.get(key),
        'ranks': ranks,
        'description': vi_desc[key],
        # The game stores line breaks as a literal backslash-n and bullets as a middle dot.
        'descriptionEn': en_list[i]['desc'].replace('\\n', '\n').replace('·', '•'),
    })

out_doc = {
    'source': 'Simplified Tech Tree for 1v1 Arabia - Survivalist',
    'sourceUrl': 'https://aoe2-de-tools.herokuapp.com/civ-ranking/',
    'sourceVersion': 'Age of Empires II: Definitive Edition, cập nhật 10/12/2024',
    'rankConversion': d['rankConversion'],
    'civilizations': result,
}
json.dump(out_doc, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
units = sorted({u for c in result for b in c['ranks'].values() for u in b})
print(len(result), 'civs,', len(units), 'units')
print(units)
