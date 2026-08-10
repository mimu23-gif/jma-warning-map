'use strict';
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..', '..');
const BOUNDARY_DIR = path.join(REPO_ROOT, 'data', 'boundaries');
// GASの admin_buildRegionIndex が生成したものをDriveから落として置く場所（gitignore対象）
const INDEX_PATH = path.join(REPO_ROOT, '_work', 'region-index.json');

/** region-index.json の raw/norm6 から fileId -> ローカルパス の対応表を作る */
function buildFileIdMap() {
  const idx = JSON.parse(fs.readFileSync(INDEX_PATH, 'utf8'));
  const map = new Map();
  for (const dictName of ['raw', 'norm6']) {
    const dict = idx[dictName] || {};
    for (const key of Object.keys(dict)) {
      const entry = dict[key];
      if (!entry || !entry.i || map.has(entry.i)) continue;
      // entry.l はDrive上のフォルダ名（1saibun 等）。ローカルでは data/boundaries/ 配下に対応する。
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
  const fileIdMap = buildFileIdMap();
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
          // クォート内の文字列だけを取り出してローカルファイルへマッピングする。
          const m = /"([^"]*)"/.exec(query || '');
          const name = m ? m[1] : '';
          const knownFiles = {
            'region-index.json': INDEX_PATH
          };
          const p = knownFiles[name];
          let used = !p || !fs.existsSync(p);
          return {
            hasNext: () => !used,
            next: () => {
              used = true;
              return { getBlob: () => makeBlob(fs.readFileSync(p, 'utf8')) };
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

module.exports = { createGasMocks, buildFileIdMap };
