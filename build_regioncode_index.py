import json
from pathlib import Path
from typing import Dict, List, Set, Any


def read_geojson(file_path: Path) -> Any:
    with file_path.open("r", encoding="utf-8") as f:
        return json.load(f)


def iter_features(obj: Any):
    if isinstance(obj, dict):
        if "features" in obj and isinstance(obj["features"], list):
            for feat in obj["features"]:
                if isinstance(feat, dict):
                    yield feat
        elif obj.get("type") == "Feature":
            yield obj
        else:
            # Unknown object structure; best effort: scan for nested features arrays
            for value in obj.values():
                if isinstance(value, dict) or isinstance(value, list):
                    yield from iter_features(value)
    elif isinstance(obj, list):
        for item in obj:
            yield from iter_features(item)


def extract_regioncodes_from_file(file_path: Path) -> Set[str]:
    regioncodes: Set[str] = set()
    try:
        obj = read_geojson(file_path)
    except Exception:
        return regioncodes

    for feature in iter_features(obj):
        if not isinstance(feature, dict):
            continue
        props = feature.get("properties", {})
        if not isinstance(props, dict):
            continue
        rc = props.get("regioncode")
        if rc is None:
            # 一部のデータでは "code" が regioncode と同義の場合がある
            rc = props.get("code")
        if rc is None:
            continue
        # 正規化（数字文字列を想定。その他は文字列化）
        if isinstance(rc, (int, float)):
            rc = str(int(rc))
        else:
            rc = str(rc).strip()
        if rc:
            regioncodes.add(rc)
    return regioncodes


def main() -> None:
    workspace = Path(__file__).resolve().parent.parent
    targets = [
        workspace / "GAS" / "1saibun",
        workspace / "GAS" / "hukenyohoukutou",
        workspace / "GAS" / "sikutyousonnwomatometatiikitou",
        workspace / "GAS" / "sityousontou",
    ]

    index: Dict[str, List[Dict[str, str]]] = {}
    missing: List[Dict[str, str]] = []

    for folder in targets:
        if not folder.exists():
            continue
        for file_path in sorted(folder.glob("*.geojson")):
            regioncodes = extract_regioncodes_from_file(file_path)
            if not regioncodes:
                missing.append({
                    "folder": folder.name,
                    "filename": file_path.name,
                    "path": str(file_path.relative_to(workspace)),
                })
                continue
            for rc in regioncodes:
                index.setdefault(rc, [])
                entry = {
                    "folder": folder.name,
                    "filename": file_path.name,
                    "path": str(file_path.relative_to(workspace)),
                }
                # 同じ rc で同じファイルを重複登録しない
                if entry not in index[rc]:
                    index[rc].append(entry)

    out_dir = workspace / "GAS" / "taiouhyou"
    out_dir.mkdir(parents=True, exist_ok=True)

    # JSON 出力
    out_json = out_dir / "index_regioncode.json"
    with out_json.open("w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=2)

    # CSV 出力（regioncode,folder,filename,path）
    out_csv = out_dir / "index_regioncode.csv"
    with out_csv.open("w", encoding="utf-8", newline="") as f:
        f.write("regioncode,folder,filename,path\n")
        for rc, files in sorted(index.items(), key=lambda x: x[0]):
            for meta in files:
                f.write(
                    f"{rc},{meta['folder']},{meta['filename']},{meta['path']}\n"
                )

    # regioncode が見つからなかったファイル一覧
    out_missing = out_dir / "missing_regioncode_files.csv"
    with out_missing.open("w", encoding="utf-8", newline="") as f:
        f.write("folder,filename,path\n")
        for meta in missing:
            f.write(f"{meta['folder']},{meta['filename']},{meta['path']}\n")

    print(
        json.dumps(
            {
                "files_scanned": sum(1 for _ in sum((list(p.glob('*.geojson')) for p in targets if p.exists()), [])),
                "regioncode_keys": len(index),
                "missing_files": len(missing),
                "output_json": str(out_json.relative_to(workspace)),
                "output_csv": str(out_csv.relative_to(workspace)),
                "output_missing_csv": str(out_missing.relative_to(workspace)),
            },
            ensure_ascii=False,
            indent=2,
        )
    )


if __name__ == "__main__":
    main()


