#!/usr/bin/env python3
"""
気象庁 予報区等GIS（シェープファイル）→ GeoJSON 変換＋都道府県別分割スクリプト。

出典: 気象庁 予報区等GISデータ
  https://www.data.jma.go.jp/developer/gis.html
  （世界測地系 JGD2011 / シェープファイル / 全国一括ZIP）

本アプリが使う4区分と、ダウンロードするZIP・出力フォルダ・ファイル名サフィックスの対応:

（出力先はいずれも data/boundaries/ 配下）

  一次細分区域等             : 20190125_AreaForecastLocalM_1saibun_GIS.zip      -> 1saibun/                        *_area.geojson
  府県予報区等               : 20190125_AreaForecastLocalM_prefecture_GIS.zip   -> hukenyohoukutou/                *_forecast.geojson
  市町村等をまとめた地域等   : 20230517_AreaForecastLocalM_matome_GIS.zip       -> sikutyousonnwomatometatiikitou/ *_region.geojson
  市町村等（気象警報・注意報）: 20260226_AreaInformationCity_weather_GIS.zip     -> sityousontou/                   *_region.geojson

各シェープファイル（.zip のまま / 展開後の .shp どちらでも可）を読み込み、地域コードの
先頭2桁（都道府県コード）で分割して、上記フォルダへ <pref2>_<romaji>_<suffix>.geojson を
出力します（既存の命名規則に一致）。出力後、各フォルダを Drive にアップロードし、
GAS の admin_buildRegionIndex を実行すると region-index.json が生成されます。

依存: geopandas（fiona / pyproj / shapely を含む）
  pip install geopandas

使い方:
  # 4つのZIP（または展開後の .shp）を _work/gis_src/ に置いて実行（既定値のまま実行すればよい）
  python tools/build_geojson_folders.py
"""
import argparse
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

try:
    import geopandas as gpd
except ImportError:
    sys.exit("geopandas が必要です: pip install geopandas")

# 都道府県コード（先頭2桁）-> ファイル名用の英語名
PREF_NAMES = {
    "01": "hokkaido", "02": "aomori", "03": "iwate", "04": "miyagi", "05": "akita",
    "06": "yamagata", "07": "fukushima", "08": "ibaraki", "09": "tochigi", "10": "gunma",
    "11": "saitama", "12": "chiba", "13": "tokyo", "14": "kanagawa", "15": "niigata",
    "16": "toyama", "17": "ishikawa", "18": "fukui", "19": "yamanashi", "20": "nagano",
    "21": "gifu", "22": "shizuoka", "23": "aichi", "24": "mie", "25": "shiga",
    "26": "kyoto", "27": "osaka", "28": "hyogo", "29": "nara", "30": "wakayama",
    "31": "tottori", "32": "shimane", "33": "okayama", "34": "hiroshima", "35": "yamaguchi",
    "36": "tokushima", "37": "kagawa", "38": "ehime", "39": "kochi", "40": "fukuoka",
    "41": "saga", "42": "nagasaki", "43": "kumamoto", "44": "oita", "45": "miyazaki",
    "46": "kagoshima", "47": "okinawa",
}

# 出力フォルダ / ファイル名サフィックス / ダウンロードZIP名 / 入力名のマッチ語
TARGETS = [
    {"folder": "1saibun",                        "suffix": "area",     "zip": "20190125_AreaForecastLocalM_1saibun_GIS.zip",    "match": "1saibun"},
    {"folder": "hukenyohoukutou",                "suffix": "forecast", "zip": "20190125_AreaForecastLocalM_prefecture_GIS.zip", "match": "prefecture"},
    {"folder": "sikutyousonnwomatometatiikitou", "suffix": "region",   "zip": "20230517_AreaForecastLocalM_matome_GIS.zip",     "match": "matome"},
    {"folder": "sityousontou",                   "suffix": "region",   "zip": "20260226_AreaInformationCity_weather_GIS.zip",   "match": "AreaInformationCity_weather"},
]

# 地域コード列の自動検出候補（旧 GAS.py 準拠）
CODE_COL_CANDIDATES = [
    "regioncode", "RegionCode", "REGIONCODE",
    "code", "CODE", "JCODE",
    "AREACODE", "AreaCode", "area_code",
    "groupcode", "GroupCode", "GROUPCODE",
    "REGION_CD", "region_cd",
]


def find_input(src, target):
    """src 内から対象の入力（完全一致ZIP優先、無ければ match を含む .zip/.shp）を返す。"""
    exact = src / target["zip"]
    if exact.exists():
        return exact
    cands = []
    for pat in ("*.zip", "*.shp"):
        for p in src.glob(pat):
            if target["match"].lower() in p.name.lower():
                cands.append(p)
    return sorted(cands)[0] if cands else None


def read_gdf(path):
    """シェープを読み込む。DBFはShift_JIS(cp932)のことが多いので順に試す。"""
    last_err = None
    for enc in ("cp932", "utf-8"):
        try:
            return gpd.read_file(path, encoding=enc)
        except Exception as e:
            last_err = e
    try:
        return gpd.read_file(path)
    except Exception as e:
        raise RuntimeError("読み込みに失敗: %s (%s)" % (path, last_err or e))


def detect_code_col(gdf):
    for c in CODE_COL_CANDIDATES:
        if c in gdf.columns:
            return c
    return None


def main():
    ap = argparse.ArgumentParser(description="JMA予報区等GIS シェープ→GeoJSON＋都道府県分割")
    ap.add_argument("--src", default=str(REPO_ROOT / "_work" / "gis_src"),
                    help="ダウンロードしたZIP/SHPを置いたディレクトリ")
    ap.add_argument("--out", default=str(REPO_ROOT / "data" / "boundaries"),
                    help="出力フォルダ群を作る基準ディレクトリ")
    args = ap.parse_args()

    src = Path(args.src)
    out_base = Path(args.out)
    if not src.exists():
        sys.exit("--src が存在しません: %s" % src)

    total = 0
    for t in TARGETS:
        inp = find_input(src, t)
        if not inp:
            print("[skip] 入力が見つかりません: %s（%s）" % (t["zip"], t["folder"]))
            continue

        gdf = read_gdf(inp)

        # 座標系を EPSG:4326（lon/lat）へ。LeafletはWGS84 lon/lat前提。JGD2011は実質WGS84。
        try:
            if gdf.crs is not None:
                gdf = gdf.to_crs(4326)
        except Exception:
            pass

        col = detect_code_col(gdf)
        if not col:
            print("[warn] コード列を特定できません: %s cols=%s" % (inp.name, list(gdf.columns)))
            continue

        out_dir = out_base / t["folder"]
        out_dir.mkdir(parents=True, exist_ok=True)

        # コードを文字列化し、admin_buildRegionIndex が読む 'regioncode' を必ず持たせる
        gdf["regioncode"] = gdf[col].astype(str).str.strip()
        gdf["_pref"] = gdf["regioncode"].str[:2]

        n_files = 0
        for pref2, sub in gdf.groupby("_pref"):
            romaji = PREF_NAMES.get(pref2)
            if not romaji:
                continue  # 全国・地方など都道府県に属さないコードはスキップ
            sub = sub.drop(columns=["_pref"])
            out_path = out_dir / ("%s_%s_%s.geojson" % (pref2, romaji, t["suffix"]))
            if out_path.exists():
                out_path.unlink()
            sub.to_file(out_path, driver="GeoJSON")  # UTF-8
            n_files += 1
        print("[ok] %s -> %s/ : %d ファイル（コード列=%s）" % (inp.name, t["folder"], n_files, col))
        total += n_files

    print("完了: 合計 %d ファイル出力" % total)


if __name__ == "__main__":
    main()
