# 警報発令エリア 可視化（C# / Windows標準機能のみ版）

[`gas/`](../gas/) の Google Apps Script 版を、**Windowsに最初から入っているものだけ**で動く
C#アプリへ移植したものです。ローカルにHTTPサーバを立て、既定ブラウザで地図を表示します。

- **インストール不要** — 単一の `JmaMap.exe`（43KB）と設定ファイルだけ
- **管理者権限不要** — `localhost` に固定バインドするのでURL ACL登録もファイアウォール許可も要らない
- **メモ帳だけで作れる** — ソースは全てプレーンテキスト。`.csproj` も `.ico` も `.resx` も使わない
- **追加ランタイム不要** — .NET Framework 4.8（Windows 10/11 同梱）のみ。NuGetパッケージゼロ

## 必要なもの

| 項目 | 備考 |
|---|---|
| Windows 10 / 11 | .NET Framework 4.8 が同梱されている世代 |
| `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` | Windows同梱のC#コンパイラ。ビルドに使う |
| ブラウザ | 既定ブラウザ（Edge/Chrome/Firefox いずれも可） |
| インターネット接続 | 気象庁の警報フィードと地図タイルの取得に必要 |

Visual Studio、.NET SDK、NuGet、MSBuild、Node.js は **いずれも不要** です。

## ビルド

`build.bat` をダブルクリックするだけです。

```
Compiler: C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe
Building JmaMap.exe ...

BUILD OK -> ...\csharp\JmaMap.exe
```

## 実行

`JmaMap.exe` をダブルクリックすると、サーバが起動して既定ブラウザに地図が開きます。
常駐中はタスクトレイにアイコンが出るので、右クリックから操作してください。

| メニュー | 動作 |
|---|---|
| ブラウザで開く | 地図をもう一度開く（アイコンのダブルクリックでも可） |
| 索引を再構築 | 境界GeoJSONを差し替えたときに実行 |
| ログを開く | `jmamap.log` を開く |
| 終了 | サーバを止めて常駐解除 |

### コマンドライン引数

| 引数 | 用途 |
|---|---|
| `--noopen` | サーバだけ起動してブラウザを開かない |
| `--selftest` | GUIを出さずに索引構築・警報取得・応答生成を一通り試し、`selftest.txt` に結果を書く |
| `--console` | 呼び出し元のコンソールにログを出す（`--selftest` と併用すると便利） |

## 設定（settings.json）

| キー | 既定値 | 意味 |
|---|---|---|
| `port` | `8787` | 待ち受けポート。埋まっていたら `portTries` 個ぶん繰り上げる |
| `portTries` | `10` | ポートを試す回数 |
| `browser` | `"default"` | `"default"`＝既定ブラウザ／`"app"`＝Edgeのアプリモード（アドレスバー無しの専用ウィンドウ） |
| `dataDir` | `"../data/boundaries"` | 境界GeoJSONの4フォルダを置いた場所 |
| `geoFolders` | 4フォルダ | 走査する境界GeoJSONのフォルダ名 |
| `pointsCsv` | `"../data/points.csv"` | POI（任意地点）CSVの場所 |
| `geoCacheFiles` | `8` | メモリに載せるGeoJSONファイル数の上限 |
| `zoomTolerances` | 下表 | ズーム別の簡略化の許容誤差（度）。`[最大ズーム, 許容誤差]` の並び |
| `indexCache` | `region-index-local.json` | 索引キャッシュの保存先 |

相対パスは `JmaMap.exe` の場所が基準です。

### ズーム連動の詳細度（LOD）

同梱データは許容誤差 0.0002度（約22m）で作ってあり、**サーバがズームに応じてさらに間引いてから送ります**。
遠景では粗い形状で軽く、近景では同梱データそのままの精度になります。間引いた結果はメモリに
キャッシュするので、同じ段の2回目以降は再計算しません。

| ズーム | 許容誤差 | 実測の応答 | 頂点 | 想定する見え方 |
|---|---|---|---|---|
| 〜5 | 0.02（約2.2km） | **1.04 MB** | 16,855 | 全国表示 |
| 6〜7 | 0.008（約890m） | 2.03 MB | 38,802 | 地方表示 |
| 8〜9 | 0.003（約330m） | 4.74 MB | 99,275 | 県表示 |
| 10〜11 | 0.001（約110m） | 12.78 MB | 278,211 | 市郡表示 |
| 12〜13 | 0.0005（約56m） | 23.18 MB | 509,451 | 市街表示 |
| 14〜 | 0（同梱データそのまま・約22m） | 47.84 MB | 1,056,423 | 最大ズーム |

（全国1,309地域に警報が出ている状態での実測。いずれも未解決0件）

画面側は起動時に `/api/status` からこの表を受け取り、ズームが別の段に入ったときだけ
取り直します（400msのデバウンス付き、表示位置は動かしません）。

近景の47.8MBをさらに減らすには、表示範囲外のエリアを送らない「ビューポート絞り込み」が有効です
（最大ズームでは画面内に数エリアしか入らないため）。現状は未実装です。

## 構成

```
csharp\
  build.bat                 csc.exe を叩くだけのビルドスクリプト
  settings.json             設定（GAS版の Script Properties に相当）
  src\
    Program.cs      333行   起動・二重起動抑止・トレイ・ブラウザ起動・自己診断
    Server.cs       439行   HttpListener・ルーティング・FeatureCollection生成
    Settings.cs      91行   settings.json の読み込みとパス解決
    GeoIndex.cs     274行   コード→ファイル索引の構築／キャッシュ／GeoJSONのLRU
    GeoJson.cs      137行   GeoJSONを走査してフィーチャ位置を拾うスキャナ
    Json.cs         240行   軽量JSONパーサ／ライタ
    JmaClient.cs    264行   気象庁フィードの取得と警報の集約
    PointsCsv.cs    224行   POI CSV の読み込み
  web\
    map.html                画面（GAS版 MAP.html を fetch() 版に改修）
    leaflet.js / .css       Leaflet 1.9.4（BSD-2-Clause）を同梱
```

## GeoTool.exe（境界データ変換ツール）

気象庁の予報区等GIS（シェープファイル）から、このアプリが使う都道府県別GeoJSONを作るCLIです。
`tools/build_geojson_folders.py`（geopandas必須）と同じ成果物を、**Python無しで**作れます。

```
GeoTool.exe convert     --in <ZIP|SHP> --out <出力フォルダ> [--suffix area] [--encoding cp932] [--layer 名前の一部]
GeoTool.exe shp2geojson --in <ZIP|SHP> --out <全国版.geojson>
GeoTool.exe split       --in <全国版.geojson> --out <出力フォルダ> [--suffix area]
GeoTool.exe merge       --in <都道府県別フォルダ> --out <全国版.geojson>
GeoTool.exe simplify    --in <フォルダ|ファイル> --out <同> --tolerance 0.0023
```

### 同梱データの作り方（2段構え）

原データは4区分で 1.88GB・4,502万頂点あり、そのまま配信するとブラウザが持ちません。
**変換 → 簡略化** の2段で作ります。

```
:: 1) 気象庁ZIP -> 高精度な都道府県別GeoJSON（1.88GB）
GeoTool.exe convert  --in ..\_work\gis_src\20230517_AreaForecastLocalM_matome_GIS.zip ^
                     --out ..\_work\boundaries_full\sikutyousonnwomatometatiikitou

:: 2) 表示用に間引く（14.4MB）
GeoTool.exe simplify --in ..\_work\boundaries_full\sikutyousonnwomatometatiikitou ^
                     --out ..\data\boundaries\sikutyousonnwomatometatiikitou ^
                     --tolerance 0.0023
```

`--tolerance` は度で指定します（緯度1度 ≒ 111km なので 0.0023度 ≒ 256m）。同梱データはこの値です。

| 許容誤差 | 削減率 | 全4区分の合計 | 用途 |
|---|---|---|---|
| 0.0001（約11m） | 約85% | 約150 MB | ズーム18でも輪郭が保たれる |
| 0.001（約111m） | 約98% | 約30 MB | 中間 |
| **0.0023（約256m）** | **99.3%** | **14.4 MB** | **同梱データ。全国表示〜市町村表示向け** |
| 0.005（約557m） | 99.6% | 約8 MB | 全国表示専用 |

簡略化の副作用: 境界線が許容誤差ぶんずれるほか、**3頂点未満に潰れたリング（小島・岩礁）は削除されます**
（北海道の一次細分区域でポリゴン 9,435 → 157）。離島のPOIが警報エリア判定から外れる可能性があります。
各ポリゴンを独立に簡略化するため、隣接区域の境界に隙間が生じることもあります。

例（配布ZIPをそのまま渡せます。展開不要）:

```
GeoTool.exe convert --in ..\_work\gis_src\20230517_AreaForecastLocalM_matome_GIS.zip ^
                    --out ..\data\boundaries\sikutyousonnwomatometatiikitou
```

### 実装上の要点

- **座標変換をしない**のが成立の鍵です。気象庁の予報区等GISはJGD2011の*地理座標*（経緯度）で配布され、
  JGD2011とWGS84の差はセンチメートル級なので、EPSG:4326としてそのまま出力できます
  （Python版の `to_crs(4326)` も実質無変換）。投影座標系（PROJCS）の入力は `.prj` を見て拒否します
- シェープファイルはリングを平坦に並べるだけなので、**外周と穴は符号付き面積で判別**します
  （ESRI仕様：外周=時計回り、穴=反時計回り）
- 座標は「doubleの厳密な10進展開を小数15桁へ正しく丸めた表記」で書きます。
  .NET Frameworkの `ToString("F15")` は有効数字15桁で頭打ちになり精度が落ちるため、
  `BigInteger` で厳密に計算しています
- ZIP内のエントリ名は日本語かつ古いものはCP932のまま（UTF-8フラグ無し）なので、
  **名前を見ずに拡張子だけで取り出します**
- DBFの文字コードは `.cpg` →（無ければ）レコード領域のUTF-8妥当性→CP932 の順で判定します

### 検証結果

| 検証 | 結果 |
|---|---|
| `split` の往復（既存188ファイル→全国版→再分割） | **188/188 が SHA256 まで完全一致** |
| `shp2geojson` vs geopandas（同一シェープファイル） | 属性・ジオメトリ型・座標構造すべて一致、**座標2,113,096点が誤差0.0** |
| 気象庁の実配布ZIP4本からの `convert` | 全て変換成功（Python版は府県予報区ZIPの読み込みに失敗する） |

同一ZIPからPython版と比較すると、次の点で**C#版のほうが忠実**です。

- **属性の文字コード**: 1saibun のDBFは実際にはUTF-8（言語ドライバID=0x00・`.cpg`無し）。
  GDALはCP932と誤認して `津軽` を `豢･霆ｽ` にしてしまうが、本ツールは正しく読む
- **座標**: GDALは短い表記に丸める際に数ULPずれることがある（例: 真値が `140.45000000000005` でも
  `140.45` と書く）。本ツールは読み取ったdoubleを厳密に書き出す
- **ZIPの互換性**: GDALの `/vsizip` はCP932エントリ名のZIPを開けないことがあるが、本ツールは影響を受けない

## エンドポイント

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/` | `web/map.html` |
| GET | `/leaflet.js`, `/leaflet.css` | 同梱のLeaflet |
| GET | `/api/points` | `{items, types1..3, labels}` |
| POST | `/api/warnings` | `{levels, phenomena}` を受け取り FeatureCollection を返す |
| GET | `/api/status` | ポートと索引の状態 |

## GAS版との対応

| GAS | この移植版 |
|---|---|
| `doGet()` + `HtmlService` | `HttpListener` が `map.html` を返す |
| `google.script.run` | `fetch()` |
| `DriveApp` + `region-index.json`（DriveファイルID） | ローカルフォルダの直読み（パスが識別子） |
| `SpreadsheetApp` | `data/points.csv` |
| `CacheService`（95KB上限） | メモリ上のキャッシュ（上限対策が不要になった） |
| `PropertiesService` | `settings.json` |
| `assertOwner_()` | ローカル単独実行のため概念ごと不要 |
| 6分の実行時間制限 | なし |

ロジック（コードの6桁/7桁正規化、`class10s` の子コード展開、政令市の親コードへのフォールバック、
レベル判定、現象名の対応表）はGAS版と同じ規則を移してあります。

## 実測値（このリポジトリのデータ・2026-08-10時点）

| 項目 | 結果 |
|---|---|
| 索引の構築 | 188ファイル・2602フィーチャ・raw 2210件・norm6 2123件 / **3.4秒**（GAS版 `admin_buildRegionIndex` と同一の件数） |
| 索引キャッシュからの復元 | **9ミリ秒** |
| 警報の取得と集約 | 705地域 / 約290ミリ秒 |
| 全国分の応答生成 | 45.7MB・705フィーチャ・**未解決0件** / 294ミリ秒 |
| POI読み込み | 30件 / 13ミリ秒 |

## メモ帳だけで編集するときの注意

1. **C# 5 の構文で書く** — Windows同梱の `csc.exe` はRoslyn以前のもので、`$"..."`（文字列補間）、
   `?.`、`nameof`、式形式メンバー、タプルは使えません（`async`/`await`・LINQ・`var` は使えます）
2. **保存はUTF-8** — Windows 11のメモ帳の既定。BOMの有無どちらでも日本語リテラルは正しく通ります
3. **`build.bat` を保存するときは「ファイルの種類: すべてのファイル」を選ぶ** — 既定のままだと
   `build.bat.txt` になってしまいます
4. **`build.bat` のコメントはASCIIで書く** — cmd.exe のコードページはCP932のため、日本語コメントは化けます
5. **バイナリを増やさない** — トレイアイコンは `SystemIcons` を借りているので `.ico` は不要です

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| 「境界GeoJSONが見つかりません」 | `settings.json` の `dataDir` / `geoFolders` を確認 |
| 「ポートを確保できませんでした」 | `settings.json` の `port` を変更 |
| 警報の取得に失敗する | ネットワークとプロキシ設定を確認。詳細は `jmamap.log` |
| 境界データを差し替えた | トレイの「索引を再構築」を実行 |
| 動作を詳しく見たい | `JmaMap.exe --selftest --console` |

## 出典・ライセンス

このディレクトリの**コード**はリポジトリ直下の [MIT License](../LICENSE) に従います。

同梱・表示するデータはMITの対象外です。

- **境界GeoJSON**（[`../data/boundaries/`](../data/boundaries/)）:
  「気象庁『予報区等GISデータ』（https://www.data.jma.go.jp/developer/gis.html ）を加工して作成」
  （シェープファイルをGeoJSONへ変換し、都道府県別に分割）
- **警報・注意報**（実行時に表示）:
  出典「気象庁ホームページ（https://www.jma.go.jp/bosai/ ）」。本アプリは**気象庁が発表した情報を表示**する
  ものであり、独自の予報・警報を行うものではありません
- **地図タイル**: &copy; OpenStreetMap contributors
- **Leaflet 1.9.4**（`web/leaflet.js`, `web/leaflet.css`）: BSD-2-Clause

気象庁由来のデータ・情報の利用は
[気象庁ホームページの利用規約](https://www.jma.go.jp/jma/kishou/info/coment.html)
（公共データ利用規約 第1.0版に準拠。**出典の明記**および**加工した旨の明記**が必要）に従ってください。
