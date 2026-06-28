/*** =========================================
 *  任意地点（POI）レイヤ用
 *  - スプレッドシート（MAPシート: name,type1,type2,type3,address,lat,lng）を読み込み
 *  - type1/type2/type3 はそれぞれ独立した分類軸として扱う
 *    （例: type1=観光/施設/駅、type2=史跡/行政/展望台、type3=避難所/公園 等）
 *  - getPoints(): クライアントへ { items, types1, types2, types3 } を返す
 *  - Code.js の putCacheSafe_ を再利用
 * ========================================== */

// SPREADSHEET_ID は利用者ごとに異なるため、ここには書かず Script Properties
// （実行 > プロジェクトの設定 > スクリプト プロパティ、キー: POINTS_SPREADSHEET_ID）で設定する。
// 設定は admin_setPointsSpreadsheetId() からも行える。
const POINTS_CFG = Object.freeze({
  SHEET_NAME: 'MAP',
  CACHE_SEC: 6 * 60 * 60
});

/** Script Properties で読み込み先スプレッドシートIDを設定 */
function admin_setPointsSpreadsheetId(spreadsheetId) {
  assertOwner_();  // 管理操作は所有者のみ（assertOwner_ は Code.js で定義）
  const id = String(spreadsheetId || '').trim();
  if (!id) throw new Error('spreadsheetId が空です');
  PropertiesService.getScriptProperties().setProperty('POINTS_SPREADSHEET_ID', id);
  return { ok: true, spreadsheetId: id };
}

/** CFG + Script Properties を合成した実効設定を返す */
function getPointsConfig_() {
  const sp = PropertiesService.getScriptProperties();
  return Object.freeze(Object.assign({}, POINTS_CFG, {
    SPREADSHEET_ID: sp.getProperty('POINTS_SPREADSHEET_ID') || ''
  }));
}

// 論理名 -> シートヘッダの別名（小文字 / 日本語）
const POINTS_HEADER_ALIASES = Object.freeze({
  name: ['name', '名称', '地点名', '地点名称'],
  type1: ['type1', '種別1', 'category1', 'type', '種別'],
  type2: ['type2', '種別2', 'category2'],
  type3: ['type3', '種別3', 'category3'],
  address: ['address', '住所'],
  lat: ['lat', 'latitude', '緯度'],
  lng: ['lng', 'lon', 'long', 'longitude', '経度']
});

/** クライアントAPI: 地点一覧と種別一覧を返す */
function getPoints() {
  const cache = CacheService.getScriptCache();
  const key = 'POINTS_SHEET_V3';
  const cached = cache.get(key);
  if (cached) {
    try { return JSON.parse(cached); } catch (_) {}
  }

  const result = loadPointsFromSheet_();
  putCacheSafe_(cache, key, JSON.stringify(result), POINTS_CFG.CACHE_SEC);
  return result;
}

/** スプレッドシートの MAP シートを読み、{items, types1, types2, types3} へ変換する */
function loadPointsFromSheet_() {
  const cfg = getPointsConfig_();
  if (!cfg.SPREADSHEET_ID) {
    throw new Error('POINTS_SPREADSHEET_ID が未設定です。Script Properties に設定するか admin_setPointsSpreadsheetId(spreadsheetId) を実行してください。');
  }
  const ss = SpreadsheetApp.openById(cfg.SPREADSHEET_ID);
  const sheet = ss.getSheetByName(cfg.SHEET_NAME) || ss.getSheets()[0];
  if (!sheet) return { items: [], types1: [], types2: [], types3: [], labels: {} };

  const rows = sheet.getDataRange().getValues();
  return parsePointsRows_(rows);
}

/** シートの2次元配列(先頭行=ヘッダ) -> {items, types1, types2, types3, labels} */
function parsePointsRows_(rows) {
  if (!rows || rows.length < 2) return { items: [], types1: [], types2: [], types3: [], labels: {} };

  const rawHeader = rows[0].map(h => String(h || '').trim());
  const header = rawHeader.map(h => h.toLowerCase());
  const colIndex = (logicalName) => {
    const aliases = POINTS_HEADER_ALIASES[logicalName] || [logicalName];
    for (const a of aliases) {
      const i = header.indexOf(a);
      if (i !== -1) return i;
    }
    return -1;
  };
  const idx = {
    name: colIndex('name'),
    type1: colIndex('type1'),
    type2: colIndex('type2'),
    type3: colIndex('type3'),
    address: colIndex('address'),
    lat: colIndex('lat'),
    lng: colIndex('lng')
  };

  // フィルタ見出し用: シートのヘッダー文字列をそのまま採用（無ければ既定の表記）
  const labels = {
    type1: (idx.type1 !== -1 && rawHeader[idx.type1]) || '種別1',
    type2: (idx.type2 !== -1 && rawHeader[idx.type2]) || '種別2',
    type3: (idx.type3 !== -1 && rawHeader[idx.type3]) || '種別3'
  };

  const items = [];
  const type1Set = new Set();
  const type2Set = new Set();
  const type3Set = new Set();

  for (let r = 1; r < rows.length; r++) {
    const row = rows[r];
    if (!row || row.every(c => String(c).trim() === '')) continue;

    const get = (i) => (i !== -1 && row[i] != null) ? String(row[i]).trim() : '';
    const lat = parseFloat(get(idx.lat));
    const lng = parseFloat(get(idx.lng));
    if (!isFinite(lat) || !isFinite(lng)) continue; // 座標未設定・不正行はスキップ

    const type1 = get(idx.type1);
    const type2 = get(idx.type2);
    const type3 = get(idx.type3);

    items.push({
      name: get(idx.name) || '(無題)',
      type1, type2, type3,
      address: get(idx.address),
      lat,
      lng
    });
    type1Set.add(type1);
    type2Set.add(type2);
    type3Set.add(type3);
  }

  return {
    items,
    types1: Array.from(type1Set).sort(),
    types2: Array.from(type2Set).sort(),
    types3: Array.from(type3Set).sort(),
    labels
  };
}

/** デバッグ: キャッシュを無視して再読込した結果を確認 */
function debug_getPoints() {
  const result = loadPointsFromSheet_();
  console.log(`items: ${result.items.length}件, types1: [${result.types1.join(', ')}]`);
  if (result.items.length === 0) {
    const cfg = getPointsConfig_();
    const ss = SpreadsheetApp.openById(cfg.SPREADSHEET_ID);
    console.log(`シート一覧: [${ss.getSheets().map(s => s.getName()).join(', ')}]`);
    const sheet = ss.getSheetByName(cfg.SHEET_NAME);
    if (sheet) {
      const rows = sheet.getDataRange().getValues();
      console.log(`行数: ${rows.length}, ヘッダー: [${(rows[0] || []).join(', ')}]`);
    }
  }
  return result;
}
