# 警報発令エリア 可視化マップ

気象庁の特別警報・警報・注意報の発表状況と、任意の地点（POI）をひとつの地図上に重ねて表示するアプリです。

![スクリーンショット](docs/screenshot.png)

**本体は C# 版（[`csharp/`](csharp/)）です。** Windowsに最初から入っているものだけで動き、
インストール・管理者権限・追加ランタイム・Googleアカウントのいずれも要りません。

元はGoogle Apps Script（GAS）のWebアプリとして作ったもので、同じ画面とロジックのGAS版を
[`gas/`](gas/) に残してあります（→ [GAS版](#gas版googleアカウントで共有したい場合)）。

## クイックスタート

1. リポジトリをクローン（境界データも同梱されているので追加のダウンロードは不要）
2. [csharp/build.bat](csharp/build.bat) をダブルクリック → `JmaMap.exe`（約43KB）が生成される
3. `JmaMap.exe` を実行 → 既定ブラウザに地図が開き、タスクトレイに常駐する

Visual Studio / .NET SDK / NuGet / MSBuild / Node.js は不要です。Windows同梱の
`csc.exe`（.NET Framework 4.8）だけでビルドします。`localhost` に固定バインドするため、
ファイアウォール許可もURL ACL登録も要りません。

セットアップ・設定項目・トラブルシューティングの詳細は [csharp/README.md](csharp/README.md) を参照してください。

## ディレクトリ構成

```
├─ csharp/    本体（C#・Windows標準機能のみ）
├─ gas/       Apps Script 版（clasp の rootDir）
├─ data/
│   ├─ boundaries/   警報エリアの境界GeoJSON（4区分・188ファイル・約127MB）
│   │                ＋ tsunami/ 津波予報区の海岸線（66区・約25MB）
│   ├─ points.csv    POI（任意地点）のサンプルデータ
│   └─ regioncode/   地域コード対応表（ローカル集計スクリプトの出力）
├─ tools/     境界データの変換・集計スクリプト（Python・GeoTool.exe があれば不要）
├─ dev/
│   ├─ node/         GAS APIのモック（Node.jsだけでGAS版のロジックを検証する）
│   ├─ gas-debug/    調査用のGASスニペット（clasp の push 対象外）
│   └─ samples/      調査時に保存した警報データのスナップショット
├─ docs/      スクリーンショット等
└─ _work/     作業用。巨大な生データ・旧試作・気象庁のPDF資料など（Git管理外）
```

### 本体（`csharp/`）

| ファイル | 役割 |
|---|---|
| [csharp/build.bat](csharp/build.bat) | `csc.exe` を叩くだけのビルドスクリプト |
| [csharp/settings.json](csharp/settings.json) | 設定（ポート・データの場所・ズーム別の詳細度） |
| [csharp/src/Program.cs](csharp/src/Program.cs) | 起動・二重起動抑止・トレイ常駐・ブラウザ起動・自己診断 |
| [csharp/src/Server.cs](csharp/src/Server.cs) | `HttpListener` のルーティングと FeatureCollection 生成 |
| [csharp/src/JmaClient.cs](csharp/src/JmaClient.cs) | 気象庁フィードの取得と警報の集約 |
| [csharp/src/DisasterClient.cs](csharp/src/DisasterClient.cs) | 地震（震源・震度）と台風の取得 |
| [csharp/src/GeoIndex.cs](csharp/src/GeoIndex.cs) | 地域コード→ファイル索引の構築・キャッシュ・GeoJSONのLRU |
| [csharp/src/PointsCsv.cs](csharp/src/PointsCsv.cs) | POI CSV の読み込み |
| [csharp/web/map.html](csharp/web/map.html) | フロントエンド（Leaflet地図・フィルタUI） |
| [csharp/GeoTool.exe](csharp/geotool/) | 境界データ変換ツール（シェープファイル→GeoJSON。Python不要） |

ブラウザからアクセスすると `map.html` が返され、クライアント側から `fetch()` で
`/api/warnings` / `/api/points` を呼び出します。

## データソース

### 警報・災害データ（リアルタイム）

気象庁が公開している以下のエンドポイントをサーバー側から直接取得しています。
ローカルにダンプを保持しているわけではなく、毎回ライブで取得します。

- `https://www.jma.go.jp/bosai/warning/data/r8/map.json` — 全国の現況警報・注意報フィード
- `https://www.jma.go.jp/bosai/common/const/area.json` — 地域コードの階層情報

C#版はこれに加えて、いま発生している災害を**別レイヤー**として重ねられます（既定はオフ）。
「地上の人と建築物に影響しうる警報」を対象にしており、海上警報は含めていません。

| レイヤー | 取得元 | 結合するコード |
|---|---|---|
| 地震・震度 | `bosai/quake/data/list.json` | 市町村（7桁） |
| 台風 | `bosai/typhoon/data/targetTc.json` ＋ TC別 `forecast.json` / `specifications.json` | 緯度経度を直描き |
| 噴火警報 | `bosai/volcano/data/warning.json` | 市町村（7桁） |
| 降灰予報 | 防災情報XML `eqvol.xml` → VFVO53 | 市町村（7桁） |
| 指定河川洪水予報 | 防災情報XML `extra.xml` → VXKO76 | **府県予報区（6桁）のみ** |
| 津波警報・注意報 | `bosai/tsunami/data/list.json` ＋ 個別報 | 津波予報区（3桁・専用の海岸線データ） |

市町村コードで来るものは、警報と同じ解決規則（7桁一致 → 6桁 → 政令市の親コードへフォールバック）が
そのまま使えます。

> **指定河川洪水予報だけは河川の形で描けません。** 電文に市町村コードが無く、気象庁は河川区間の
> GISデータも配布していないため、地図に結合できるのは府県予報区コードだけです。府県単位で塗り、
> 河川名・レベル・区間の説明は一覧で補っています。

詳細は [csharp/README.md](csharp/README.md#災害情報レイヤー地震台風) を参照してください。

### 警報エリアの境界データ（ポリゴン）

地図上に警報エリアを塗るためのポリゴンは、気象庁の
[予報区等GISデータ](https://www.data.jma.go.jp/developer/gis.html)
（**シェープファイル・全国一括ZIP・世界測地系JGD2011**）を GeoJSON に変換し、都道府県別に分割したものです。

**変換・分割済みのGeoJSONをリポジトリに同梱している**ため、クローン後すぐ動きます
（4フォルダ・188ファイル・合計約127MB）。本アプリが使う4区分と元データの対応:

| 同梱フォルダ（`data/boundaries/` 配下） | 気象庁GISの区分 | 元データ（更新時のダウンロード元ZIP） |
|---|---|---|
| `1saibun/` | 一次細分区域等 | `20190125_AreaForecastLocalM_1saibun_GIS.zip` |
| `hukenyohoukutou/` | 府県予報区等 | `20190125_AreaForecastLocalM_prefecture_GIS.zip` |
| `sikutyousonnwomatometatiikitou/` | 市町村等をまとめた地域等 | `20230517_AreaForecastLocalM_matome_GIS.zip` |
| `sityousontou/` | 市町村等（**気象警報・注意報**） | `20260226_AreaInformationCity_weather_GIS.zip` |
| `tsunami/` | 津波予報区（**線データ**・66区） | `20240520_AreaTsunami_GIS.zip` |

#### 同梱データは「表示用に間引いた版」です

気象庁の原データは4区分で合計 **1.88GB・4,502万頂点** あり、そのまま地図に送ると
1回の取得が526MBになってブラウザが実用に耐えません（JSON.parseだけで約5秒・ヒープ約2.5GB）。
そのため **許容誤差 0.0002度（約22m）でDouglas-Peucker簡略化した版**（127MB・292万頂点）を同梱し、
さらに**サーバがズームに応じて動的に間引いてから送る**構成にしています。

| ズーム | 送信時の許容誤差 | 応答サイズ | 頂点 |
|---|---|---|---|
| 〜5（全国表示） | 約2.2km | **1.04 MB** | 16,855 |
| 8〜9（県表示） | 約330m | 4.74 MB | 99,275 |
| 12〜13（市街表示） | 約56m | 23.18 MB | 509,451 |
| 14〜（最大ズーム） | 同梱データそのまま（約22m） | 47.84 MB | 1,056,423 |

全国1,309地域に警報が出ている状態での実測値です。従来データでは全国表示でも45.7MBだったので、
遠景は**44分の1**に軽くなり、近景は従来より精細になりました。段の切り替えは
[csharp/settings.json](csharp/settings.json) の `zoomTolerances` で調整できます
（全段の内訳は [csharp/README.md](csharp/README.md#ズーム連動の詳細度lod) 参照）。

さらにC#版は、ズーム8以上では**表示範囲に重ならないエリアを送りません**。ズーム14・全国1,840地域が
発表中の状態で、東京付近だけに絞ると 63,505KB → **793KB**（80分の1）になります。

簡略化の副作用（把握したうえで採用しています）:

- 境界線が許容誤差ぶんずれます（近景でも最大約22m）
- 潰れたリング（小さな島や岩礁）は削除されます。遠景ほど顕著で、
  離島にあるPOIが「警報発令エリア内の地点」に出なくなる可能性があります

原データからの再生成はコマンド1つで済むため、リポジトリには間引いた版だけを置いています。

#### データを更新する場合（気象庁の最新版へ差し替え）

同梱データは特定時点のスナップショットです。最新の境界に更新したいときだけ、元データから再生成します。

1. 上表のZIPを[配布ページ](https://www.data.jma.go.jp/developer/gis.html)からダウンロードし
   `_work/gis_src/` に置く（`.zip` のままで可）
2. シェープ → GeoJSON 変換＋都道府県分割 → 表示用の簡略化

   ```
   csharp\GeoTool.exe convert  --in _work\gis_src\20230517_AreaForecastLocalM_matome_GIS.zip ^
                               --out _work\boundaries_full\sikutyousonnwomatometatiikitou
   csharp\GeoTool.exe simplify --in _work\boundaries_full\sikutyousonnwomatometatiikitou ^
                               --out data\boundaries\sikutyousonnwomatometatiikitou ^
                               --tolerance 0.0002
   ```

   → `<都道府県コード>_<英名>_<種別>.geojson`（例: `10_gunma_area.geojson`）が再生成されます。
   コマンドの詳細は [csharp/README.md](csharp/README.md#geotoolexe境界データ変換ツール) を参照してください。
3. C#版はトレイの「索引を再構築」、GAS版は `admin_buildRegionIndex` で索引を作り直す

> **参考: Python版**（[tools/build_geojson_folders.py](tools/build_geojson_folders.py)・`pip install geopandas` が必要）
> でも同じ成果物を作れますが、DBFの文字コード判定・ZIPのエントリ名（CP932）・座標の丸めの3点で
> GeoTool.exe のほうが忠実に読めることを実データで確認しているため、そちらを推奨します
> （Python版は府県予報区ZIPの読み込みに失敗します）。

> 補足: 「市町村等」は6種（気象警報／土砂災害／河川洪水／大雨危険度／地震津波／火山）あり、本アプリは
> **気象警報・注意報（`weather`）** を使用。ZIP名先頭の日付は更新日で将来変わります。シェープの属性（DBF）は
> Shift_JIS（cp932）が多く、`.cpg` → UTF-8妥当性 → CP932 の順で判定します。座標系 JGD2011 は WGS84 互換で、
> 出力は EPSG:4326（lon/lat）です。

#### データの出典・ライセンス

- **境界GeoJSON（同梱）**:
  「気象庁『予報区等GISデータ』（https://www.data.jma.go.jp/developer/gis.html ）を加工して作成」
  （シェープファイルをGeoJSONへ変換し、都道府県別に分割・表示用に簡略化）。
- **警報・注意報／地震（震源・震度）／台風（実行時に表示）**:
  出典「気象庁ホームページ（https://www.jma.go.jp/bosai/ ）」。本アプリは**気象庁が発表した情報を表示**するもので、
  独自の予報・警報を行うものではありません。

これら気象庁由来のデータ・情報の利用は、
[気象庁ホームページの利用規約](https://www.jma.go.jp/jma/kishou/info/coment.html)（公共データ利用規約 第1.0版に準拠。
**出典の明記**および**加工した旨の明記**が必要）に従ってください。本リポジトリのコードの[MITライセンス](LICENSE)は、
これら気象庁由来のデータ・情報には適用されません。

### 発表中の警報種別だけをフィルタに出す

警報取得のAPIは、**絞り込みをかける前の全国の警報**から「いま発表されている現象コードと
レベル」を集めて `available` として返します。画面はこれを見て、発表されていない種別のチップを隠します。

```js
available: { levels: ['chuui', 'keihou'], codes: ['05','07','08','10','14','15','16','20','21','29'] }
```

- 上の例（発表中10現象）では、現象チップ18種のうち8種だけを表示します
- 特別警報・危険警報が出ていなければ、レベル選択もその2つが消えます
- 隠れた種別は「他 n 種別（発表なし）も表示」を押せば出せます
- 発表が始まった種別は次回取得時に自動で表示・選択へ戻ります
- 「リセット」で全種別の表示に戻ります

絞り込み後の結果から求めると「利用者が外した種別」と「そもそも発表がない種別」を区別できないため、
サーバ側で絞り込み前の集合を作っています。C#版・GAS版で同じ仕様です。

### POI（任意地点）データ

`name,type1,type2,type3,address,lat,lng` の列構成で読み込みます。
`type1`〜`type3` 列のヘッダー文字列はそのままUIのフィルタ見出しとして表示されます。

- C#版: [data/points.csv](data/points.csv)（場所は `settings.json` の `pointsCsv`）
- GAS版: Googleスプレッドシートの1シート（[gas/Points.js](gas/Points.js) の `POINTS_CFG` / `POINTS_HEADER_ALIASES` 参照）

## GAS版（Googleアカウントで共有したい場合）

同じ画面とロジックのGoogle Apps Script版を [`gas/`](gas/) に残しています。URLを配って
複数人に見せたい場合や、Windows以外から使いたい場合はこちらです。

| ファイル | 役割 |
|---|---|
| [gas/appsscript.json](gas/appsscript.json) | GASプロジェクトのマニフェスト |
| [gas/Code.js](gas/Code.js) | `doGet()` エントリーポイント／気象庁 `r8/map.json` フィードから現況警報を集約し、GeoJSON境界と結合して返す |
| [gas/Points.js](gas/Points.js) | Googleスプレッドシートから任意地点（POI）データを読み込み、種別ごとにグルーピングして返す |
| [gas/Message_acquisition.js](gas/Message_acquisition.js) | 警報データ取得に関する補助ロジック |
| [gas/MAP.html](gas/MAP.html) | フロントエンド（Leaflet地図・フィルタUI） |
| [gas/debug_area_json_fallback.js](gas/debug_area_json_fallback.js) | 気象庁 `area.json` 周りのデバッグ用関数 |

ブラウザからアクセスすると `MAP.html` が返され、クライアント側から
`google.script.run` 経由で `getActiveWarningFeatures()` / `getPoints()` などを呼び出します。

### C#版との対応

| C#版（本体） | GAS版 |
|---|---|
| `HttpListener` が `web/map.html` を返す | `doGet()` / `HtmlService` |
| `fetch()` | `google.script.run` |
| ローカルフォルダの直読み（起動時に索引を構築・キャッシュ） | `DriveApp` + `region-index.json`（DriveファイルID） |
| `data/points.csv` | `SpreadsheetApp` |
| メモリ上のキャッシュ | `CacheService`（95KB上限） |
| [csharp/settings.json](csharp/settings.json) | `PropertiesService`（Script Properties） |
| ローカル単独実行のため不要 | `assertOwner_()` |
| 実行時間の制限なし | 6分の実行時間制限 |

地域コードの6桁/7桁正規化、`class10s` の子コード展開、政令市の親コードへのフォールバック、
レベル判定といったロジックは両版で同じ規則です。索引も同じ188ファイルを同じ規則で走査するため、
同梱データに対しては同じ件数（2386フィーチャ・raw 2351件・norm6 2294件）になります。

### 初期設定（Script Properties）

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

### セットアップ手順

1. [data/boundaries/](data/boundaries/) の4フォルダを Google Drive の `AREA_GEO_PARENT_ID` フォルダ配下にアップロード
2. GASの `admin_buildRegionIndex` を実行して `region-index.json` を生成（下記）

### 正規化パイプライン（`region-index.json`）

GAS版はDriveのファイルIDでGeoJSONを引くため、「地域コード → DriveファイルID」の対応表が必要です
（C#版はローカルのパスが識別子になるので、この対応表は起動時に自動で組み立てられます）。

`AREA_GEO_PARENT_ID` 配下のGeoJSONをDrive上で走査して `region-index.json` を生成し、
同じフォルダへ書き戻す管理関数を [gas/Code.js](gas/Code.js) に用意しています。Apps Scriptエディタで
`admin_buildRegionIndex` を実行するだけで再生成でき、ローカル処理は不要です（実行は所有者のみ）。

```js
admin_buildRegionIndex();
// 同梱データなら次の件数になります（C#版の索引構築で確認した値）
// => { ok: true, files: 188, features: 2386, rawCount: 2351, norm6Count: 2294 }
```

- Driveのファイル列挙からファイルIDが自動で得られるため、ローカル処理にあった
  「パス → Drive ファイルID」の変換手順が不要です。
- 生成後はサーバーキャッシュ（`region-index.json` のキャッシュ）も自動で破棄します。
- 出力構造（`loadRegionIndex_` / `findIndexEntryForCode_` が読む形）:
  - `raw`: 原コード（7桁等）→ `{ i: ファイルID, … }`
  - `norm6`: 6桁正規化コード → `{ i: ファイルID, r: 代表コード, … }`

なお `region-index.json` は多数のDrive ファイルIDを含むため、リポジトリには含めていません
（`.gitignore` 対象）。

Drive権限が無い環境向けに、ローカルのGeoJSONを集計する補助スクリプトも同梱しています
（ファイルパスベースの対応表のみを出力し、Drive ファイルIDは付与しません）。

1. [tools/build_regioncode_index.py](tools/build_regioncode_index.py)
   `data/boundaries/` 配下4フォルダの `*.geojson` を走査し、各フィーチャの
   `properties.regioncode`（または `code`）を集計して
   `data/regioncode/index_regioncode.json` / `.csv` を出力します。
2. [tools/analyze_region_index.py](tools/analyze_region_index.py)
   `_work/region-index.json` を読み、構造や地域ごとの内訳を検証・分析する補助スクリプトです。

いずれもリポジトリのどこから実行してもよく、パスはスクリプト自身の位置から解決します。

### ローカル開発（Node.jsモック）

GAS環境を使わずにNode.jsだけでGAS版のロジックを検証できるよう、簡易モック環境を用意しています。

```
node dev/node/server.js     # ブラウザで http://localhost:8787/ を開く
node dev/node/run.js        # 画面を出さずに集計結果だけ確認する
```

[dev/node/gas-mocks.js](dev/node/gas-mocks.js) が `UrlFetchApp` / `DriveApp` / `SpreadsheetApp` /
`CacheService` / `PropertiesService` をローカルファイルで再現し、
[dev/node/server.js](dev/node/server.js) がポート8787でHTTPサーバーとして起動します。
境界GeoJSONは [data/boundaries/](data/boundaries/)、POIデータは [data/points.csv](data/points.csv) を
スプレッドシート代わりに使用します。

`region-index.json` は **モックが `data/boundaries/` を走査して自動で組み立てます**（`admin_buildRegionIndex`
と同じ構造。Drive ファイルIDの代わりに「フォルダ名/ファイル名」を識別子にします）。Driveから持ってくる必要は
なく、境界データを作り直しても索引が古いままにならずに済みます。結果は `_work/region-index-mock.json` に
キャッシュされ、データが変わったときだけ作り直します。

### デプロイ

[clasp](https://github.com/google/clasp) でソースを同期します。push対象は [gas/](gas/) の中身だけなので、
`.clasp.json` に **`"rootDir": "gas"`** を指定してください（`.claspignore` は不要です）。
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

本リポジトリの**コード**は [MIT License](LICENSE) です。
ただし `csharp/web/leaflet.js` / `csharp/web/leaflet.css` は同梱した
[Leaflet](https://leafletjs.com/) 1.9.4（BSD-2-Clause）であり、同ライブラリのライセンスに従います。
地図タイルは &copy; OpenStreetMap contributors です。

ただし、同梱の境界GeoJSON（`data/boundaries/` 配下）および実行時に表示する警報・注意報・地震・台風は**気象庁由来のデータ・情報**であり、
MITは適用されません。これらの利用は[気象庁ホームページの利用規約](https://www.jma.go.jp/jma/kishou/info/coment.html)
（公共データ利用規約 第1.0版準拠）に従い、**出典の明記**と**加工した旨の明記**が必要です
（詳細は本文「データの出典・ライセンス」を参照）。
