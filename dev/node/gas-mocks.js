'use strict';
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..', '..');
const BOUNDARY_DIR = path.join(REPO_ROOT, 'data', 'boundaries');
const INDEX_CACHE = path.join(REPO_ROOT, '_work', 'region-index-mock.json');

/**
 * data/boundaries/ を走査して region-index.json 相当を組み立てる。
 * GASの admin_buildRegionIndex と同じ構造（raw / norm6）を作り、Drive ファイルIDの代わりに
 * 「フォルダ名/ファイル名」を識別子として使う。
 *
 * Driveで生成した region-index.json をわざわざ持ってこなくてもモックが動くようにするため。
 * 境界データを作り直したときに索引が古いままになる事故も防げる。
 */
function buildRegionIndex() {
  const folders = fs.existsSync(BOUNDARY_DIR)
    ? fs.readdirSync(BOUNDARY_DIR).filter(d => fs.statSync(path.join(BOUNDARY_DIR, d)).isDirectory())
    : [];

  const files = [];
  for (const folder of folders.sort()) {
    const dir = path.join(BOUNDARY_DIR, folder);
    for (const name of fs.readdirSync(dir).sort()) {
      if (!name.endsWith('.geojson')) continue;
      files.push({ folder, name, full: path.join(dir, name) });
    }
  }

  // 中身が変わっていなければ前回の結果を使う（毎回126MB走査すると遅いため）
  const signature = files
    .map(f => { const st = fs.statSync(f.full); return f.folder + '/' + f.name + ':' + st.size + ':' + st.mtimeMs; })
    .join('|');
  if (fs.existsSync(INDEX_CACHE)) {
    try {
      const cached = JSON.parse(fs.readFileSync(INDEX_CACHE, 'utf8'));
      if (cached.signature === signature) return cached.index;
    } catch (_) { /* 壊れていたら作り直す */ }
  }

  const raw = {};
  const norm6 = {};
  for (const f of files) {
    const text = fs.readFileSync(f.full, 'utf8');
    const id = f.folder + '/' + f.name;
    const meta = { f: f.folder, i: id, n: f.name, l: f.folder };

    // 全体をJSON.parseすると重いので、properties のコードだけを拾う
    let re = /"regioncode"\s*:\s*"([^"]*)"/g;
    let codes = [];
    let m;
    while ((m = re.exec(text)) !== null) codes.push(m[1]);
    if (codes.length === 0) {
      re = /"code"\s*:\s*"([^"]*)"/g;
      while ((m = re.exec(text)) !== null) codes.push(m[1]);
    }

    for (const code of codes) {
      if (!code) continue;
      if (!raw[code]) raw[code] = meta;
      const digits = String(code).replace(/\D/g, '');
      if (digits.length !== 6 && digits.length !== 7) continue;
      const key6 = digits.length === 7 ? digits.slice(0, 6) : digits;
      if (!norm6[key6]) norm6[key6] = { f: meta.f, i: meta.i, n: meta.n, l: meta.l, r: code, t: 'any' };
    }
  }

  const index = { version: '2', updatedAt: new Date().toISOString(), folders: {}, raw, norm6 };
  try {
    fs.mkdirSync(path.dirname(INDEX_CACHE), { recursive: true });
    fs.writeFileSync(INDEX_CACHE, JSON.stringify({ signature, index }), 'utf8');
  } catch (_) { /* 書けなくても動作には影響しない */ }
  return index;
}

/** fileId（= フォルダ名/ファイル名）-> ローカルパス の対応表 */
function buildFileIdMap(index) {
  const map = new Map();
  for (const dictName of ['raw', 'norm6']) {
    const dict = index[dictName] || {};
    for (const key of Object.keys(dict)) {
      const entry = dict[key];
      if (!entry || !entry.i || map.has(entry.i)) continue;
      map.set(entry.i, path.join(BOUNDARY_DIR, entry.l, entry.n));
    }
  }
  return map;
}

function makeBlob(text) {
  return { getDataAsString: () => text };
}

/**
 * GAS のグローバルAPI（UrlFetchApp / DriveApp / CacheService / PropertiesService）を
 * ローカルファイル & 事前フェッチ済みHTTPレスポンスで再現するモック群を作る。
 * @param {Map<string,{status:number, body:string}>} prefetched URL -> レスポンス
 */
function createGasMocks(prefetched) {
  const regionIndex = buildRegionIndex();
  const regionIndexJson = JSON.stringify(regionIndex);
  const fileIdMap = buildFileIdMap(regionIndex);
  const cacheStore = new Map();
  const propsStore = new Map();

  // 本番は Script Properties に利用者ごとのIDが入る。モックのDriveApp/SpreadsheetAppは
  // IDの中身を見ずローカルファイルへ流すので、未設定エラーを避けるためダミーを入れておく。
  propsStore.set('AREA_GEO_PARENT_ID', 'local-mock-folder');
  propsStore.set('POINTS_SPREADSHEET_ID', 'local-mock-spreadsheet');

  const UrlFetchApp = {
    fetch(url) {
      const hit = prefetched.get(url);
      if (!hit) throw new Error('UrlFetchApp.fetch: 未プリフェッチのURL: ' + url);
      return {
        getResponseCode: () => hit.status,
        getContentText: () => hit.body
      };
    },
    fetchAll(requests) {
      return requests.map(req => this.fetch(req.url));
    }
  };

  const DriveApp = {
    getFileById(fileId) {
      const p = fileIdMap.get(fileId);
      if (!p) throw new Error('DriveApp.getFileById: 未知のfileId: ' + fileId);
      return { getBlob: () => makeBlob(fs.readFileSync(p, 'utf8')) };
    },
    getFolderById(folderId) {
      return {
        searchFiles(query) {
          // findFileInFolderByName_ は `title = "name"` 等の形でクエリを渡すため、
          // クォート内の文字列だけを取り出して判定する。
          // region-index.json はローカルの境界データから組み立てたものを返す。
          const m = /"([^"]*)"/.exec(query || '');
          const name = m ? m[1] : '';
          let used = (name !== 'region-index.json');
          return {
            hasNext: () => !used,
            next: () => {
              used = true;
              return { getBlob: () => makeBlob(regionIndexJson) };
            }
          };
        }
      };
    }
  };

  // 本番は実際のスプレッドシート(MAPシート)を参照するが、ローカルでは
  // Sheets APIに接続せず、リポジトリ内の points.csv をシート代わりに使う。
  const SpreadsheetApp = {
    openById(_id) {
      return {
        getSheetByName(_name) {
          const csvPath = path.join(REPO_ROOT, 'data', 'points.csv');
          if (!fs.existsSync(csvPath)) return null;
          const rows = parseCsv(fs.readFileSync(csvPath, 'utf8'));
          return { getDataRange: () => ({ getValues: () => rows }) };
        }
      };
    }
  };

  const CacheService = {
    getScriptCache() {
      return {
        get: key => (cacheStore.has(key) ? cacheStore.get(key) : null),
        put: (key, value) => cacheStore.set(key, value),
        remove: key => cacheStore.delete(key)
      };
    }
  };

  const PropertiesService = {
    getScriptProperties() {
      return {
        getProperty: key => (propsStore.has(key) ? propsStore.get(key) : null),
        setProperty: (key, value) => propsStore.set(key, value)
      };
    }
  };

  const Utilities = { sleep: () => {}, parseCsv };

  return { UrlFetchApp, DriveApp, SpreadsheetApp, CacheService, PropertiesService, Utilities };
}

/** GASのUtilities.parseCsvを模した最小実装（クォート/エスケープ対応） */
function parseCsv(text) {
  const rows = [];
  let field = '', row = [], inQuotes = false;
  const pushField = () => { row.push(field); field = ''; };
  const pushRow = () => { pushField(); rows.push(row); row = []; };

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inQuotes) {
      if (c === '"') {
        if (text[i + 1] === '"') { field += '"'; i++; }
        else inQuotes = false;
      } else field += c;
    } else if (c === '"') {
      inQuotes = true;
    } else if (c === ',') {
      pushField();
    } else if (c === '\n') {
      pushRow();
    } else if (c === '\r') {
      // 改行はLFで処理するためCRは無視
    } else {
      field += c;
    }
  }
  if (field.length || row.length) pushRow();
  while (rows.length && rows[rows.length - 1].every(c => c === '')) rows.pop();
  return rows;
}

module.exports = { createGasMocks, buildRegionIndex, buildFileIdMap };
