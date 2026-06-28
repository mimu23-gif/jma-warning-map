# 警報発令エリア 可視化マップ（Google Apps Script）

気象庁の特別警報・警報・注意報の発表状況と、任意の地点（POI）をひとつの地図上に重ねて表示する
Google Apps Script（GAS）製の Web アプリです。

![スクリーンショット](Screenshot/スクリーンショット%202026-06-28%20193024.png)

## 構成

| ファイル | 役割 |
|---|---|
| [appsscript.json](appsscript.json) | GASプロジェクトのマニフェスト |
| [Code.js](Code.js) | `doGet()` エントリーポイント／気象庁 `r8/map.json` フィードから現況警報を集約し、GeoJSON境界と結合して返す |
| [Points.js](Points.js) | Googleスプレッドシートから任意地点（POI）データを読み込み、種別ごとにグルーピングして返す |
| [Message_acquisition.js](Message_acquisition.js) | 警報データ取得に関する補助ロジック |
| [MAP.html](MAP.html) | フロントエンド（Leaflet地図・フィルタUI） |
| [debug_area_json_fallback.js](debug_area_json_fallback.js) | 気象庁 `area.json` 周りのデバッグ用関数 |

ブラウザからアクセスすると `MAP.html` が返され、クライアント側から
`google.script.run` 経由で `getActiveWarningFeatures()` / `getPoints()` などを呼び出します。

## データソース

### 警報データ（リアルタイム）

気象庁が公開している以下のエンドポイントをサーバー側（`Code.js`）から直接取得しています。
ローカルにダンプを保持しているわけではなく、毎回ライブで取得します。

- `https://www.jma.go.jp/bosai/warning/data/r8/map.json` — 全国の現況警報・注意報フィード
- `https://www.jma.go.jp/bosai/common/const/area.json` — 地域コードの階層情報

### 警報エリアの境界データ（ポリゴン）

地図上に警報エリアを塗るためのポリゴンは、気象庁の
[予報区等GISデータ](https://www.data.jma.go.jp/developer/gis.html)
（**シェープファイル・全国一括ZIP・世界測地系JGD2011**）を GeoJSON に変換し、都道府県別に分割したものです。
`Code.js` が Google Drive 上のこれらを `DriveApp` 経由で読み込みます。

**Python等のローカル環境が用意できなくても使えるよう、変換・分割済みのGeoJSONをリポジトリに同梱しています**
（4フォルダ・合計約193MB・単一ファイル最大約6MB）。本アプリが使う4区分と元データの対応:

| 同梱フォルダ | 気象庁GISの区分 | 元データ（更新時のダウンロード元ZIP） |
|---|---|---|
| `1saibun/` | 一次細分区域等 | `20190125_AreaForecastLocalM_1saibun_GIS.zip` |
| `hukenyohoukutou/` | 府県予報区等 | `20190125_AreaForecastLocalM_prefecture_GIS.zip` |
| `sikutyousonnwomatometatiikitou/` | 市町村等をまとめた地域等 | `20230517_AreaForecastLocalM_matome_GIS.zip` |
| `sityousontou/` | 市町村等（**気象警報・注意報**） | `20260226_AreaInformationCity_weather_GIS.zip` |

#### 使い方（クローン後・Python不要）

1. 同梱の4フォルダ（`1saibun/` 等）を Google Drive の `AREA_GEO_PARENT_ID` フォルダ配下にアップロード
2. GASの `admin_buildRegionIndex` を実行して `region-index.json` を生成（[正規化パイプライン](#正規化パイプライン)参照）

#### データを更新する場合（気象庁の最新版へ差し替え・geopandasが必要）

同梱データは特定時点のスナップショットです。最新の境界に更新したいときだけ、元データから再生成します。

1. 上表のZIPを[配布ページ](https://www.data.jma.go.jp/developer/gis.html)からダウンロードし `gis_src/` に置く（`.zip` のままで可）
2. シェープ → GeoJSON 変換＋都道府県分割（[build_geojson_folders.py](build_geojson_folders.py)）:
   ```
   pip install geopandas
   python build_geojson_folders.py --src ./gis_src --out .
   ```
   → 4フォルダの `<都道府県コード>_<英名>_<種別>.geojson`（例: `10_gunma_area.geojson`）が再生成されます。

> 補足: 「市町村等」は6種（気象警報／土砂災害／河川洪水／大雨危険度／地震津波／火山）あり、本アプリは
> **気象警報・注意報（`weather`）** を使用。ZIP名先頭の日付は更新日で将来変わります。シェープの属性（DBF）は
> Shift_JIS（cp932）が多く、スクリプトは cp932 → utf-8 の順で読み込みます。座標系 JGD2011 は WGS84 互換で、
> 出力は EPSG:4326（lon/lat）です。シェープ解析や大容量GeoJSONの分割はGASの実行時間・メモリ制限に不向きなため、
> この更新処理のみローカル（geopandas）で行う構成です。

#### データの出典・ライセンス

同梱の境界GeoJSONは、気象庁「予報区等GISデータ」を加工（GeoJSON変換・都道府県分割）して作成したものです。
出典: [気象庁 予報区等GISデータ](https://www.data.jma.go.jp/developer/gis.html)（利用は気象庁ホームページの
利用規約に従ってください）。本リポジトリのコードの[MITライセンス](LICENSE)は、これら気象庁由来のデータには
適用されません。

### 正規化パイプライン

取得したGeoJSON群から「地域コード → GeoJSONファイル」の対応表（`region-index.json`）を生成します。
`Code.js` はこのファイルをDriveから読み、地域コードからGeoJSONファイルIDを引いています。

#### 推奨：GASで完結（`admin_buildRegionIndex`）

`AREA_GEO_PARENT_ID` 配下のGeoJSONをDrive上で走査して `region-index.json` を生成し、
同じフォルダへ書き戻す管理関数を [Code.js](Code.js) に用意しています。Apps Scriptエディタで
`admin_buildRegionIndex` を実行するだけで再生成でき、ローカル処理は不要です（実行は所有者のみ）。

```js
admin_buildRegionIndex();
// => { ok: true, files: 188, features: 2602, rawCount: 2210, norm6Count: 2123 }
```

- Driveのファイル列挙からファイルIDが自動で得られるため、ローカル処理にあった
  「パス → Drive ファイルID」の変換手順が不要です。
- 生成後はサーバーキャッシュ（`region-index.json` のキャッシュ）も自動で破棄します。
- 出力構造（`loadRegionIndex_` / `findIndexEntryForCode_` が読む形）:
  - `raw`: 原コード（7桁等）→ `{ i: ファイルID, … }`
  - `norm6`: 6桁正規化コード → `{ i: ファイルID, r: 代表コード, … }`

なお `region-index.json` は多数のDrive ファイルIDを含むため、リポジトリには含めていません
（`.gitignore` 対象）。実行時はDriveから読み込み、再生成も上記のGAS関数でDrive上に対して行います。

#### 参考：ローカル（Python）

Drive権限が無い環境向けに、ローカルのGeoJSONを集計する補助スクリプトも同梱しています
（ファイルパスベースの対応表のみを出力し、Drive ファイルIDは付与しません）。

1. [build_regioncode_index.py](build_regioncode_index.py)
   `1saibun/` `hukenyohoukutou/` `sikutyousonnwomatometatiikitou/` `sityousontou/` 配下の
   `*.geojson` を走査し、各フィーチャの `properties.regioncode`（または `code`）を集計して
   `taiouhyou/index_regioncode.json` / `.csv` を出力します。
2. [analyze_region_index.py](analyze_region_index.py)
   上記の集計結果を検証・分析するための補助スクリプトです。

### POI（任意地点）データ

Googleスプレッドシートの1シートを `name,type1,type2,type3,address,lat,lng` 列構成で読み込みます
（[Points.js](Points.js) の `POINTS_CFG` / `POINTS_HEADER_ALIASES` 参照）。
`type1`〜`type3` 列のヘッダー文字列はそのままUIのフィルタ見出しとして表示されます。

## 初期設定（Script Properties）

境界GeoJSONのDriveフォルダID、POIスプレッドシートIDは利用者ごとに異なるため、
ソースコードにハードコードせず Script Properties（Apps Scriptエディタ右上の
歯車アイコン → 「スクリプト プロパティ」）に設定してください。

| キー | 用途 |
|---|---|
| `AREA_GEO_PARENT_ID` | 境界GeoJSON（`region-index.json`等）を置いたDriveフォルダのID |
| `POINTS_SPREADSHEET_ID` | POI（任意地点）データを置いたGoogleスプレッドシートのID |

Apps Scriptエディタから直接実行することでも設定できます。

```js
admin_setAreaGeoFolderId('あなたのDriveフォルダID');
admin_setPointsSpreadsheetId('あなたのスプレッドシートID');
```

未設定の状態で `getFeatureByRegionCodeFlexible` / `getPoints` 等を呼ぶと、
設定が必要であることを示すエラーになります。

## ローカル開発

GAS環境を使わずにNode.jsだけでロジックを検証できるよう、簡易モック環境を用意しています。

```
node local/server.js
```

[local/gas-mocks.js](local/gas-mocks.js) が `UrlFetchApp` / `DriveApp` / `SpreadsheetApp` /
`CacheService` / `PropertiesService` をローカルファイル（`region-index.json` や `points.csv` など）で
再現し、[local/server.js](local/server.js) がポート8787でHTTPサーバーとして起動します。
POIデータはリポジトリ内の [points.csv](points.csv) をスプレッドシート代わりに使用します。

※ `region-index.json` はリポジトリに含まれないため、ローカルモックで使う場合はルート直下に別途配置してください
（GASの `admin_buildRegionIndex` で生成したものをDriveからダウンロードする等）。

## デプロイ

[clasp](https://github.com/google/clasp) でソースを同期します（`.claspignore` で対象ファイルを管理）。
`.clasp.json`（紐づくApps ScriptプロジェクトのID）は利用者ごとに異なるためリポジトリには含めていません。
初回は次のいずれかで自分のプロジェクトに紐付けてください。

```
npx clasp create --type webapp --title "警報発令エリア 可視化"
# 既存のApps Scriptプロジェクトに紐付ける場合
npx clasp clone <あなたのscriptId>
```

```
npx clasp push
```

**注意**: `clasp push` はスクリプトのHEADを更新するだけで、公開中のWebアプリURLには反映されません。
Apps Scriptエディタの「デプロイを管理」から既存デプロイを編集し、「新しいバージョン」として
デプロイし直す必要があります。

## ライセンス

[MIT License](LICENSE)
