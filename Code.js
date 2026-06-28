/*** =========================================
 *  警報発令エリア 可視化 用 Code.gs（完成版）
 *  - doGet() で MAP.html を返す
 *  - getDefaultRegionCode(): 既定コードを返す
 *  - getFeatureByRegionCodeFlexible(code, {strict}): 6桁/7桁で該当フィーチャ1件のFCを返す
 *  - region-index.json を Drive から読み、キャッシュ利用
 *  - Script Properties で DRAW_REGION_CODE を上書き可
 *  - version: 2025-09-05-0117
 * ========================================== */

/*** 設定 ***/
// AREA_GEO_PARENT_ID / DRAW_REGION_CODE は利用者ごとに異なるため、
// ここには書かず Script Properties（実行 > プロジェクトの設定 > スクリプト プロパティ）で設定する。
// 設定は admin_setAreaGeoFolderId() / admin_setDrawCode() からも行える。
const CFG = Object.freeze({
  AREA_GEO_PARENT_ID: '',                     // GeoJSON 親フォルダのDrive folder ID
  INDEX_FILE_NAME: 'region-index.json',       // 対応表ファイル名
  INDEX_CACHE_SEC: 6 * 60 * 60,               // キャッシュ秒数（6h）
  DRAW_REGION_CODE: '110021',                 // 初期描画コード（6桁 or 7桁）
  DRAW_STRICT: false                          // 7桁不一致時に6桁へフォールバックするか
});

/*** Web エンドポイント ***/
function doGet(e) {
  return HtmlService
    .createHtmlOutputFromFile('MAP')
    .setTitle('警報発令エリア 可視化');
}

/*** クライアントAPI ***/
function getDefaultRegionCode() {
  const c = getConfig_();
  return String(c.DRAW_REGION_CODE || '').trim();
}

/**
 * 6桁/7桁の地域コードから該当のGeoJSONフィーチャを1件だけ返す
 * @param {string} code - '110021' または '1100210'
 * @param {{strict?: boolean}=} options
 * @returns {object} FeatureCollection
 */
function getFeatureByRegionCodeFlexible(code, options) {
  const cfg = getConfig_();
  const strict = !!(options && options.strict) || !!cfg.DRAW_STRICT;

  const resolved = resolveCodeOrDefault_(code, cfg);
  const index = loadRegionIndex_();

  // インデックスから参照先ファイルと代表7桁を決定
  const pick = findIndexEntryForCode_(index, resolved, strict);
  if (!pick) {
    throw new Error(`該当インデックスなし: code=${resolved}, strict=${strict}`);
  }

  const feature = readSingleFeature_(pick.fileId, pick.raw7);
  if (!feature) {
    // 詳細（fileId 等の内部識別子）はサーバログのみに残し、クライアントへは汎用メッセージを返す
    console.error(`readSingleFeature_ not found: fileId=${pick.fileId}, raw7=${pick.raw7}`);
    throw new Error('該当するGeoJSONフィーチャが見つかりませんでした');
  }
  return {
    type: 'FeatureCollection',
    features: [feature]
  };
}

/*** 管理ユーティリティ（任意） ***/

/**
 * 管理操作を本プロジェクトの所有者（＝デプロイ実行ユーザー）のみに限定する防御。
 * webapp の access を ANYONE 系へ広げても、訪問者が google.script.run 経由で
 * admin_* を呼んで設定（フォルダID/シートID等）を書き換えられないようにする。
 * executeAs=USER_DEPLOYING のため getEffectiveUser() は常に所有者を返す。
 */
function assertOwner_() {
  const effective = Session.getEffectiveUser().getEmail();   // 常に所有者
  const active = Session.getActiveUser().getEmail();         // 実行中ユーザー（匿名は空文字）
  if (!active || active !== effective) {
    throw new Error('権限がありません（管理操作は所有者のみ実行できます）');
  }
}

/** Script Properties で描画コードを上書き */
function admin_setDrawCode(code) {
  assertOwner_();
  const c = String(code || '').trim();
  if (!/^\d{6,7}$/.test(c)) throw new Error('無効な地域コード形式（6桁 or 7桁）');
  PropertiesService.getScriptProperties().setProperty('DRAW_REGION_CODE', c);
  return { ok: true, code: c };
}

/** Script Properties で境界GeoJSONの親フォルダ（Drive folder ID）を設定 */
function admin_setAreaGeoFolderId(folderId) {
  assertOwner_();
  const id = String(folderId || '').trim();
  if (!id) throw new Error('folderId が空です');
  PropertiesService.getScriptProperties().setProperty('AREA_GEO_PARENT_ID', id);
  return { ok: true, folderId: id };
}

/** いまの設定を確認 */
function admin_getConfig() {
  assertOwner_();
  return getConfig_();
}

/**
 * 正規化パイプラインのGAS版：region-index.json を Drive 上のGeoJSONから再生成する。
 * AREA_GEO_PARENT_ID 直下＋サブフォルダ（1saibun / hukenyohoukutou /
 * sikutyousonnwomatometatiikitou / sityousontou など）の *.geojson / *.json を走査し、
 * 各フィーチャの regioncode/code から raw（原コード）・norm6（6桁正規化）表を構築。
 * Drive列挙でファイルIDが自動取得できるため、Python版にあった path→ID 変換の欠落が起きない。
 *
 * 出力は loadRegionIndex_ / findIndexEntryForCode_ が読む構造に一致：
 *   { version, updatedAt, parentId, folders:{id:name}, raw:{code:{f,i,n,l}}, norm6:{6桁:{f,i,n,l,r,t}} }
 * 生成後は region-index.json を親フォルダへ書き戻し、キャッシュを破棄する。
 *
 * ※ 188ファイル程度なら単発実行で収まる想定。巨大データで6分制限に当たる場合は
 *   サブフォルダ単位で分割実行する版へ拡張する（admin_buildRegionIndexForFolder_ 等）。
 *
 * @returns {{ok:boolean, files:number, features:number, rawCount:number, norm6Count:number}}
 */
function admin_buildRegionIndex() {
  assertOwner_();
  const cfg = getConfig_();
  if (!cfg.AREA_GEO_PARENT_ID) {
    throw new Error('AREA_GEO_PARENT_ID が未設定です。admin_setAreaGeoFolderId(folderId) で設定してください。');
  }
  const parent = DriveApp.getFolderById(cfg.AREA_GEO_PARENT_ID);

  const folders = {};   // subfolderId -> subfolderName
  const raw = {};       // 原コード -> {f,i,n,l}
  const norm6 = {};     // 6桁 -> {f,i,n,l,r,t}
  const stats = { files: 0, features: 0 };

  scanFolderForIndex_(parent, folders, raw, norm6, stats, cfg.INDEX_FILE_NAME);
  const subs = parent.getFolders();
  while (subs.hasNext()) {
    scanFolderForIndex_(subs.next(), folders, raw, norm6, stats, cfg.INDEX_FILE_NAME);
  }

  const index = {
    version: '2',
    updatedAt: new Date().toISOString(),
    parentId: cfg.AREA_GEO_PARENT_ID,
    folders: folders,
    raw: raw,
    norm6: norm6
  };

  writeIndexToDrive_(parent, cfg.INDEX_FILE_NAME, JSON.stringify(index));
  CacheService.getScriptCache().remove('REGION_INDEX_JSON_V2');

  return {
    ok: true,
    files: stats.files,
    features: stats.features,
    rawCount: Object.keys(raw).length,
    norm6Count: Object.keys(norm6).length
  };
}

/** 1フォルダ直下の *.geojson / *.json を走査して raw / norm6 を埋める（先勝ち） */
function scanFolderForIndex_(folder, folders, raw, norm6, stats, indexFileName) {
  const folderId = folder.getId();
  const folderName = folder.getName();
  folders[folderId] = folderName;

  const files = folder.getFiles();
  while (files.hasNext()) {
    const file = files.next();
    const name = file.getName();
    if (!/\.(geo)?json$/i.test(name)) continue;
    if (name === indexFileName) continue; // 出力先（index自身）は対象外

    let geo;
    try {
      geo = JSON.parse(file.getBlob().getDataAsString('UTF-8'));
    } catch (e) {
      console.warn('scanFolderForIndex_: parse failed for ' + name);
      continue;
    }
    stats.files++;
    const meta = { f: folderId, i: file.getId(), n: name, l: folderName };

    for (const code of iterFeatureCodes_(geo)) {
      stats.features++;
      if (!raw[code]) raw[code] = meta;            // 原コードはそのままキーに（"10" 等の短縮コードも保持）
      const norm = normalizeCodeString_(code);     // 6 or 7桁のみ返す
      if (!norm) continue;
      const key6 = (norm.length === 7) ? norm.slice(0, 6) : norm;
      if (!norm6[key6]) {
        norm6[key6] = { f: meta.f, i: meta.i, n: meta.n, l: meta.l, r: code, t: 'any' };
      }
    }
  }
}

/** GeoJSON(Feature/FeatureCollection)から各フィーチャの地域コード文字列を列挙（runtimeと同じキー優先順） */
function iterFeatureCodes_(geo) {
  const out = [];
  const pushCode = (ft) => {
    if (!ft || ft.type !== 'Feature') return;
    const p = ft.properties || {};
    const v = p.regioncode ?? p.code ?? p.Code ?? p.AREA_CODE;
    if (v === undefined || v === null) return;
    const s = (typeof v === 'number') ? String(v) : String(v).trim();
    if (s) out.push(s);
  };
  if (geo && geo.type === 'Feature') {
    pushCode(geo);
  } else if (geo && geo.type === 'FeatureCollection' && Array.isArray(geo.features)) {
    for (var i = 0; i < geo.features.length; i++) pushCode(geo.features[i]);
  }
  return out;
}

/** 親フォルダ直下の同名ファイルを上書き、無ければ新規作成して書き込む */
function writeIndexToDrive_(folder, name, content) {
  const existing = findFileInFolderByName_(folder.getId(), name);
  if (existing) {
    existing.setContent(content);
    return existing;
  }
  return folder.createFile(name, content, 'application/json');
}

/** インデックスのサマリ確認 */
function getIndexStats() {
  const idx = loadRegionIndex_();
  return {
    version: idx && idx.version,
    rawCount: idx && idx.raw ? Object.keys(idx.raw).length : 0,
    norm6Count: idx && idx.norm6 ? Object.keys(idx.norm6).length : 0
  };
}

/*** 内部実装 ***/

/** 設定をCFG + ScriptProperties で合成 */
function getConfig_() {
  const sp = PropertiesService.getScriptProperties();
  const override = {
    AREA_GEO_PARENT_ID: sp.getProperty('AREA_GEO_PARENT_ID') || CFG.AREA_GEO_PARENT_ID,
    DRAW_REGION_CODE: sp.getProperty('DRAW_REGION_CODE') || CFG.DRAW_REGION_CODE
  };
  return Object.freeze(Object.assign({}, CFG, override));
}

/** 入力codeが未指定なら既定値を採用・形式バリデーション */
function resolveCodeOrDefault_(code, cfg) {
  const c = String(code || cfg.DRAW_REGION_CODE || '').trim();
  if (!/^\d{6,7}$/.test(c)) {
    throw new Error(`無効なコード形式: ${c}`);
  }
  return c;
}

/**
 * 6桁/7桁コードから、参照ファイルID＆最終的に探す7桁コードを決定
 * @returns {{fileId:string, raw7:string}|null}
 */
function findIndexEntryForCode_(index, code, strict) {
  if (!index) return null;

  if (code.length === 7) {
    // 7桁：raw に厳密一致 → 失敗時、strict=falseなら6桁へフォールバック
    const hit = index.raw && index.raw[code];
    if (hit && hit.i) return { fileId: hit.i, raw7: code };
    if (strict) return null;
    const head6 = code.slice(0, 6);
    const n = index.norm6 && index.norm6[head6];
    if (n && n.i && n.r) return { fileId: n.i, raw7: n.r };
    return null;
  }

  // 6桁：norm6 の代表7桁へ
  const n6 = index.norm6 && index.norm6[code];
  if (n6 && n6.i && n6.r) return { fileId: n6.i, raw7: n6.r };
  return null;
}

/** region-index.json をDriveから読み、キャッシュする */
function loadRegionIndex_() {
  const cache = CacheService.getScriptCache();
  const key = 'REGION_INDEX_JSON_V2';
  const cached = cache.get(key);
  if (cached) {
    try { return JSON.parse(cached); } catch (_) {}
  }

  const cfg = getConfig_();
  if (!cfg.AREA_GEO_PARENT_ID) {
    throw new Error('AREA_GEO_PARENT_ID が未設定です。Script Properties に設定するか admin_setAreaGeoFolderId(folderId) を実行してください。');
  }
  const file = findFileInFolderByName_(cfg.AREA_GEO_PARENT_ID, cfg.INDEX_FILE_NAME);
  if (!file) {
    // folderId はサーバログのみに残す（クライアントへは内部IDを返さない）
    console.error(`index not found: name=${cfg.INDEX_FILE_NAME}, folderId=${cfg.AREA_GEO_PARENT_ID}`);
    throw new Error(`インデックスファイル（${cfg.INDEX_FILE_NAME}）が見つかりません`);
  }

  const txt = file.getBlob().getDataAsString('UTF-8');
  const json = JSON.parse(txt);

  putCacheSafe_(cache, key, JSON.stringify(json), cfg.INDEX_CACHE_SEC);
  return json;
}

/**
 * 単一GeoJSONファイルから、propertiesの各種キーで regioncode==raw7 を探してFeatureを返す
 * 受け付けキー: regioncode / code / Code / AREA_CODE
 */
function readSingleFeature_(fileId, raw7) {
  const blob = DriveApp.getFileById(fileId).getBlob();
  const geo = JSON.parse(blob.getDataAsString('UTF-8'));

  // Feature / FeatureCollection 双方対応
  if (geo.type === 'Feature') {
    return featureCodeEquals_(geo, raw7) ? geo : null;
  }
  if (geo.type === 'FeatureCollection' && Array.isArray(geo.features)) {
    for (var i = 0; i < geo.features.length; i++) {
      var ft = geo.features[i];
      if (featureCodeEquals_(ft, raw7)) return ft;
    }
  }
  return null;
}

/** Featureのpropertiesから各種キーでコード一致を確認 */
function featureCodeEquals_(feature, raw7) {
  if (!feature || feature.type !== 'Feature') return false;
  var p = feature.properties || {};
  var val = String(
    p.regioncode ?? p.code ?? p.Code ?? p.AREA_CODE ?? ''
  ).trim();
  return val === String(raw7);
}

/** フォルダ直下で名前一致のファイルを1件返す（先勝ち） */
function findFileInFolderByName_(folderId, name) {
  const folder = DriveApp.getFolderById(folderId);
  // Driveクエリはtitle / name どちらでも可の環境があるため両対応
  // まずは完全一致を優先
  let it = folder.searchFiles(`title = "${escapeQuery_(name)}"`);
  if (it.hasNext()) return it.next();

  // contains でも試す
  it = folder.searchFiles(`title contains "${escapeQuery_(name)}"`);
  if (it.hasNext()) return it.next();

  // name プロパティ系（環境により有効）
  it = folder.searchFiles(`name = "${escapeQuery_(name)}"`);
  if (it.hasNext()) return it.next();

  return null;
}

function escapeQuery_(s) {
  // バックスラッシュを先に、次に二重引用符をエスケープ（順序を逆にすると二重エスケープになる）。
  // 現状の呼び出しは定数ファイル名のみだが、将来ユーザー由来の名前を渡してもクエリインジェクションを防ぐ。
  return String(s || '').replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

/** CacheService に安全に put（サイズ上限 ~100KB 対策） */
function putCacheSafe_(cache, key, value, secs) {
  try {
    // おおよそ95KB超はスキップ
    if (value && value.length > 95 * 1024) return;
    cache.put(key, value, secs);
  } catch (_) {}
}

/*** デバッグ（任意） ***/
function debug_get_from_config() {
  const cfg = getConfig_();
  return getFeatureByRegionCodeFlexible(cfg.DRAW_REGION_CODE, { strict: cfg.DRAW_STRICT });
}

/** ========================
 *  最新警報（Message_acquisition相当）→ FeatureCollection 返却
 *  - getActiveWarnings(args): リストだけ返す
 *  - getActiveWarningFeatures(args): ジオメトリ込みで返す（MAP.html はこれを呼ぶ）
 * ======================== */
// （旧実装は削除。以下の最適化版を使用）

/** ============================
 *  最新警報 → FeatureCollection（最適化版・両階層監視対応）
 *  - GeoJSONは fileId ごとに1回だけ読む
 *  - 属性キーの差異と7桁/6桁差を吸収
 *  - 5分キャッシュで再読込を削減
 *  - areaTypes[0,1] 両階層監視でシステム完全性を保証
 *  
 *  【重要】両階層監視の必要性:
 *  - areaTypes[0]: 広域警報（支庁・府県レベル）
 *  - areaTypes[1]: 市町村警報（詳細レベル）
 *  - 広域専用警報の見逃し防止により災害対応の初動に寄与
 * ============================ */

// 公開API：MAP.html から呼ぶ
function getActiveWarningFeatures(args = {}) {
  const { items } = getActiveWarnings(args);           // Message_acquisition 準拠の収集
  if (!items || !items.length) return emptyFCWithMeta_();

  const index = loadRegionIndex_();
  const { class20sParent } = getAreaHierarchyMaps_();
  const byFile = new Map();  // fileId -> [{raw7, regionCode, item}]
  const unresolved = [];

  // 1) index で fileId/raw7 を引く（ここではDrive I/Oは発生しない）
  for (const it of items) {
    let pick = findIndexEntryForCode_(index, it.regionCode, /*strict*/ false);
    if (!pick) {
      // 政令市の区分割など、GeoJSON側がそこまで細分化されていないコードは
      // area.json の親コード（class15s/20s）へフォールバックする
      const parentCode = class20sParent[it.regionCode];
      if (parentCode) pick = findIndexEntryForCode_(index, parentCode, /*strict*/ false);
    }
    if (!pick) {
      unresolved.push({ code: it.regionCode, reason: 'INDEX_NOT_FOUND' });
      continue;
    }
    if (!byFile.has(pick.fileId)) byFile.set(pick.fileId, []);
    byFile.get(pick.fileId).push({ raw7: pick.raw7, regionCode: it.regionCode, item: it });
  }

  // 2) fileId単位でGeoJSONを1回だけ読む → ルックアップ表を構築
  const featuresOut = [];
  for (const [fileId, arr] of byFile.entries()) {
    const fc = getGeoJsonByFileIdCached_(fileId); // {type, features:[]}
    if (!fc || !Array.isArray(fc.features)) {
      for (const a of arr) unresolved.push({ code: a.regionCode, reason: 'GEOJSON_LOAD_FAILED' });
      continue;
    }

    // コード→Feature のマップを作る（7桁/6桁双方）
    const map7 = new Map(); // raw7 -> Feature
    const map6 = new Map(); // head6 -> Feature (代表的に最初の一致を採用)
    for (const f of fc.features) {
      const p = f && f.properties || {};
      const propCode = normalizeCodeString_(p.regioncode ?? p.code ?? p.Code ?? p.AREA_CODE ?? '');
      if (!propCode) continue;
      if (propCode.length === 7) {
        if (!map7.has(propCode)) map7.set(propCode, f);
        const h6 = propCode.slice(0, 6);
        if (!map6.has(h6)) map6.set(h6, f);
      } else if (propCode.length === 6) {
        if (!map6.has(propCode)) map6.set(propCode, f);
      }
    }

    // 3) raw7一致 → ダメなら6桁一致で拾う
    for (const a of arr) {
      let ft = map7.get(a.raw7);
      if (!ft) ft = map6.get(a.raw7.slice(0, 6));
      if (!ft) {
        unresolved.push({ code: a.regionCode, reason: 'FEATURE_NOT_FOUND' });
        continue;
      }
      // properties を拡張
      const baseName = (ft.properties && (ft.properties.name || ft.properties.NAM || ft.properties.NAM_JP)) || '';
      const enriched = JSON.parse(JSON.stringify(ft)); // deep copy
      enriched.properties = Object.assign({}, ft.properties || {}, {
        code: a.regionCode,
        name: baseName,
        level: a.item.level,
        codes: a.item.codes,
        kinds: a.item.kinds,
        updatedAt: a.item.updatedAt
      });
      featuresOut.push(enriched);
    }
  }

  return {
    type: 'FeatureCollection',
    features: featuresOut,
    unresolved,             // [{code, reason}]
    updatedAt: new Date().toISOString()
  };
}

/**
 * 全国の現況警報・注意報を集約した単一フィード。
 * 気象庁の公開地図（map.html）自体もこのエンドポイントを参照しており、
 * 都道府県・支庁単位の個別JSON（/bosai/warning/data/warning/{code}.json）は
 * 発表元で更新が止まっている（オリジン側で古いデータのまま固定されている）ため使用しない。
 */
const WARNING_MAP_R8_URL = 'https://www.jma.go.jp/bosai/warning/data/r8/map.json';

/**
 * area.json から必要な部分だけ抜き出してキャッシュ（フルサイズは約260KBでキャッシュ上限95KBを超えるため）。
 * - class10s: 支庁内の「地方」コード→子コード（class15s）。ジオメトリより広い区分を子コードへ展開する用。
 * - class20sParent: 市町村内の地区コード（政令市の区分割等）→親コード。ジオメトリより細かい区分の代替先を探す用。
 */
function getAreaHierarchyMaps_() {
  const cache = CacheService.getScriptCache();
  const key = 'AREA_HIERARCHY_V1';
  const hit = cache.get(key);
  if (hit) {
    try { return JSON.parse(hit); } catch (_) {}
  }
  const areaConst = fetchJsonSafe_('https://www.jma.go.jp/bosai/common/const/area.json') || {};
  const class10s = areaConst.class10s || {};
  const class20sParent = {};
  const class20s = areaConst.class20s || {};
  for (const code in class20s) {
    const p = class20s[code] && class20s[code].parent;
    if (p) class20sParent[code] = p;
  }
  const result = { class10s, class20sParent };
  try {
    const s = JSON.stringify(result);
    if (s.length < 95 * 1024) cache.put(key, s, 6 * 60 * 60);
  } catch (_) {}
  return result;
}

// 収集：全国フィード（r8/map.json）から地域単位の現況をまとめる
function getActiveWarnings(args = {}) {
  const levelsFilter = Array.isArray(args.levels) && args.levels.length ? new Set(args.levels) : null;
  const phenomFilter = Array.isArray(args.phenomena) && args.phenomena.length ? new Set(args.phenomena.map(String)) : null;

  const cache = CacheService.getScriptCache();
  const cacheKey = 'WARN_MAP_R8';
  let reports = null;
  const hit = cache.get(cacheKey);
  if (hit) {
    try { reports = JSON.parse(hit); } catch (_) {}
  }
  if (!reports) {
    reports = fetchJsonSafe_(WARNING_MAP_R8_URL) || [];
    try {
      const s = JSON.stringify(reports);
      if (s.length < 95 * 1024) cache.put(cacheKey, s, 60);
    } catch (_) {}
  }

  // class10Items は支庁内の「地方」単位（class10s）の広域コードで届くことがあり、
  // このプロジェクトのGeoJSON（1saibun配下）はその1段下のclass15s単位までしか
  // ジオメトリを持たない。area.json の class10s.children で実体のあるコードへ展開する。
  const { class10s } = getAreaHierarchyMaps_();

  // 地域コードごとに現在有効な現象コードを集約（解除済みは除外）
  // ※ フィード自体が「現象スレッドごとに最新の1件のみ」を保持しているため、
  //   時系列での上書き判定は不要（公式地図の描画ロジックと同じ前提）。
  const byArea = new Map(); // regionCode -> Set<R06コード>
  for (const rep of reports) {
    const w = rep && rep.warning;
    if (!w) continue;
    for (const itemsKey of ['class10Items', 'class20Items']) {
      const items = w[itemsKey] || [];
      for (const it of items) {
        const regionCode = String(it && it.areaCode || '').trim();
        if (!regionCode) continue;
        const c10 = class10s[regionCode];
        const targets = (c10 && Array.isArray(c10.children) && c10.children.length) ? c10.children : [regionCode];
        for (const k of (it.kinds || [])) {
          if (String(k && k.status || '').trim() === '解除') continue;
          const code = normalizeR06Code_(k && k.code);
          if (!code) continue;
          for (const t of targets) {
            if (!byArea.has(t)) byArea.set(t, new Set());
            byArea.get(t).add(code);
          }
        }
      }
    }
  }

  const items = [];
  for (const [regionCode, codeSet] of byArea.entries()) {
    const codes = Array.from(codeSet);
    const level = highestLevel_(codes);
    if (levelsFilter && !levelsFilter.has(level)) continue;
    if (phenomFilter && !codes.some(c => phenomFilter.has(c))) continue;

    items.push({
      regionCode,
      level,
      codes,
      kinds: codes.map(codeToKindName_),
      updatedAt: new Date().toISOString()
    });
  }

  return { items };
}

/** GeoJSONファイルを5分キャッシュして返す */
function getGeoJsonByFileIdCached_(fileId) {
  const key = 'GEO_' + fileId;
  const cache = CacheService.getScriptCache();
  const hit = cache.get(key);
  if (hit) { try { return JSON.parse(hit); } catch(_) {} }

  const txt = DriveApp.getFileById(fileId).getBlob().getDataAsString('UTF-8');
  const json = JSON.parse(txt);
  // サイズ制限（~95KB）を越える場合はキャッシュしない
  const s = JSON.stringify(json);
  if (s.length < 95 * 1024) cache.put(key, s, 300);
  return json;
}

function normalizeCodeString_(v) {
  // 数値/文字列混在 → 数字のみ文字列へ
  const s = String(v || '').replace(/\D/g, '');
  if (s.length === 6 || s.length === 7) return s;
  return '';
}

/** R06 現象コードを2桁ゼロ埋めの文字列へ正規化（例: 3 -> '03'） */
function normalizeR06Code_(v) {
  var s = String(v == null ? '' : v).trim().replace(/\D/g, '');
  if (!s) return '';
  if (s.length >= 2) return s.slice(-2);
  return ('0' + s).slice(-2);
}

function emptyFCWithMeta_() {
  return { type: 'FeatureCollection', features: [], unresolved: [], updatedAt: new Date().toISOString() };
}

/** URLからJSONを安全に取得 */
function fetchJsonSafe_(url) {
  try {
    var res = UrlFetchApp.fetch(url, {
      method: 'get',
      muteHttpExceptions: true,
      followRedirects: true,
      validateHttpsCertificates: true
    });
    var code = res.getResponseCode();
    if (code >= 200 && code < 300) {
      var txt = res.getContentText('UTF-8');
      return JSON.parse(txt);
    }
    console.warn('fetchJsonSafe_: HTTP ' + code + ' for ' + url);
  } catch (e) {
    console.error('fetchJsonSafe_ error: ' + e);
  }
  return null;
}

/** JMA R06コード集合から表示レベルを決定（特別＞危険＞警報＞注意） */
function highestLevel_(codes) {
  var set = new Set((codes || []).map(function(c){ return String(c).trim(); }));
  
  // 特別警報（レベル5）
  var tokubetsu = ['32','33','35','36','37','38','39'];
  for (var i=0;i<tokubetsu.length;i++){ if (set.has(tokubetsu[i])) return 'tokubetsu'; }
  
  // 危険警報（レベル4）
  var kiken = ['43','48','49'];
  for (var j=0;j<kiken.length;j++){ if (set.has(kiken[j])) return 'kiken'; }
  
  // 警報（レベル3）
  var keihou = ['02','03','04','05','06','07','08','09'];
  for (var k=0;k<keihou.length;k++){ if (set.has(keihou[k])) return 'keihou'; }
  
  // 注意報（レベル2）
  var chuui = ['10','12','13','14','15','16','17','18','19','20','21','22','23','24','25','26','27','29'];
  for (var m=0;m<chuui.length;m++){ if (set.has(chuui[m])) return 'chuui'; }
  
  return 'chuui';
}

/** R06コード→現象名の簡易マップ */
function codeToKindName_(code) {
  var m = {
    '00':'解除',
    '02':'暴風雪', '13':'風雪', '32':'暴風雪特別',
    '03':'大雨', '10':'大雨', '33':'大雨特別', '43':'大雨危険度',
    '04':'洪水', '18':'洪水',
    '05':'暴風', '15':'強風', '35':'暴風特別',
    '06':'大雪', '12':'大雪', '36':'大雪特別',
    '07':'波浪', '16':'波浪', '37':'波浪特別',
    '08':'高潮', '19':'高潮', '38':'高潮特別', '48':'高潮危険度',
    '09':'土砂災害', '29':'土砂災害', '39':'土砂特別', '49':'土砂危険度',
    '14':'雷',
    '17':'融雪',
    '20':'濃霧',
    '21':'乾燥',
    '22':'なだれ',
    '23':'低温',
    '24':'霜',
    '25':'着氷',
    '26':'着雪',
    '27':'その他'
  };
  var key = normalizeR06Code_(code);
  return m[key] || 'その他';
}

// 1) Indexの状況確認（件数だけサッと見たい）
function debug_getIndexStats() {
  const s = getIndexStats();
  console.log('IndexStats:', JSON.stringify(s, null, 2));
}

// 2) 代表1件のフィーチャ取得（7桁厳密一致の例）
function debug_feature_sample() {
  const fc = getFeatureByRegionCodeFlexible('0220100', { strict: true }); // 任意の7桁に変更可
  console.log('features:', fc.features?.length, 'firstProps:', fc.features?.[0]?.properties);
}

// 3) 最新警報→地図描画用にまとめた結果（今回の本命）
function debug_fetch_active() {
  const res = getActiveWarningFeatures({
    levels: ['tokubetsu','kiken','keihou','chuui'], // UIのレベル全部
    phenomena: [] // 必要なら ['03','33','43'] などR06コードを入れる
  });
  console.log('features=', res.features.length, 'unresolved=', res.unresolved.length);
  // 先頭数件を確認
  console.log('firstFeature.properties=', res.features[0]?.properties);
  console.log('unresolvedHead=', res.unresolved.slice(0, 10));
}
