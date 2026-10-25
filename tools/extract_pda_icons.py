"""Extract installed vanilla PDA tab sprites for local visual reference only.

Requires UnityPy. Do not redistribute the extracted game artwork with the mod.
"""
import argparse
import json
from pathlib import Path

import UnityPy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", type=Path, default=Path(
        "D:/Program Files/Steam/steamapps/common/Subnautica"))
    parser.add_argument("--out", type=Path, default=Path("build/pda-012"))
    args = parser.parse_args()
    bundle_dir = args.game / "Subnautica_Data/StreamingAssets/aa/StandaloneWindows64"
    bundles = list(bundle_dir.glob("atlases_*.bundle"))
    if len(bundles) != 1:
        raise RuntimeError(f"Expected one atlas bundle; found {len(bundles)}")
    args.out.mkdir(parents=True, exist_ok=True)
    env = UnityPy.load(str(bundles[0]))
    entries = []
    for obj in env.objects:
        if obj.type.name not in ("Sprite", "Texture2D"):
            continue
        data = obj.read()
        if obj.type.name == "Sprite" and data.m_Name.startswith("Tab"):
            image = data.image
            image.save(args.out / (data.m_Name + ".png"))
            entries.append({"name": data.m_Name, "path_id": obj.path_id, "size": image.size})
        elif obj.type.name == "Texture2D" and "Uncompressed-Tabs" in data.m_Name:
            data.image.save(args.out / "vanilla-tabs-atlas.png")
    (args.out / "source.json").write_text(json.dumps(
        {"bundle": str(bundles[0]), "sprites": entries}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Extracted {len(entries)} PDA icons to {args.out}")


if __name__ == "__main__":
    main()
