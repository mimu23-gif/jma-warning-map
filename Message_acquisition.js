/**
 * GAS: JMA 警報・注意報 収集スクリプト（最終版：北海道 class15→class20 追跡）
 * - 全国47都道府県を走査し、"今発令中（発表/継続/切替）"のみをログ出力
 * - 地域コード（6桁/7桁）と上位種別（特別警報/警報/注意報）、現象コード配列を出力
 * - 北海道(010000)だけは area.json の class15s → class20s を辿って配下コードを収集
 * - そのほかの都府県は 6桁.json を優先、なければ offices を探索
 *
 * 使い方:
 * 1) このコードを Code.gs に貼付
 * 2) 関数 runCollectWarningsAll を実行
 * 3) 表示 > ログ で結果確認
 */

/*** Config ***/
const PREF_CODES = [
    "010000","020000","030000","040000","050000","060000","070000",
    "080000","090000","100000","110000","120000","130000","140000",
    "150000","160000","170000","180000","190000","200000","210000",
    "220000","230000","240000","250000","260000","270000","280000",
    "290000","300000","310000","320000","330000","340000","350000",
    "360000","370000","380000","390000","400000","410000","420000",
    "430000","440000","450000","460000","470000"
  ];
  
  // 収集対象のステータス（解除は除外）
  const ACTIVE_STATUSES = new Set(["発表", "継続", "切替"]);
  
  // フェッチ間隔（軽い配慮）
  const FETCH_SLEEP_MS = 120;
  
  // 一時エラー時のリトライ回数
  const RETRY = 1;
  
  // area.json URL（区域定義）
  const AREA_CONST_URL = "https://www.jma.go.jp/bosai/common/const/area.json";
  
  /*** Entry point ***/
  function runCollectWarningsAll() {
    const started = new Date();
    Logger.log("--- Collect JMA Warnings (Active Only) START: %s ---", started.toISOString());
  
    let totalPref = 0;
    let totalAreas = 0;
    let totalHits = 0;
  
    // area.json を先読み
    const areaConst = fetchAreaConst_();
  
    for (const prefCode of PREF_CODES) {
      totalPref++;
  
      // この都道府県に対して取得すべき JSON のベースコードを列挙
      const sourceCodes = listWarningSourcesForPref_Final_(prefCode, areaConst);
      if (sourceCodes.length === 0) {
        Logger.log("WARN: no source codes for pref %s", prefCode);
        continue;
      }
  
      const seenRegion = new Set(); // 重複抑止
  
      for (const srcCode of sourceCodes) {
        const json = fetchWarningJsonByCode_(srcCode);
        if (!json) {
          Utilities.sleep(FETCH_SLEEP_MS);
          continue;
        }
  
        // areaTypes[0] と areaTypes[1] 両方を監視（システム完全性のため）
        const allAreas = [];
        for (const areaTypeIndex of [0, 1]) {
          const areas = (json && json.areaTypes && json.areaTypes[areaTypeIndex] && json.areaTypes[areaTypeIndex].areas)
            ? json.areaTypes[areaTypeIndex].areas : [];
          allAreas.push(...areas);
        }
        totalAreas += allAreas.length;

        for (const area of allAreas) {
          const regionCode = String(area.code || "").trim(); // 6桁 or 7桁
          if (!regionCode || seenRegion.has(regionCode)) continue;
  
          const warnings = Array.isArray(area.warnings) ? area.warnings : [];
          const activeWarnings = warnings.filter(w => ACTIVE_STATUSES.has(String(w.status || "").trim()));
          if (activeWarnings.length === 0) continue;
  
          const codesSet = new Set(activeWarnings.map(w => String(w.code || "").trim()).filter(Boolean));
          if (codesSet.size === 0) continue;
          const codes = Array.from(codesSet);
  
          const kindsSet = new Set();
          for (const code of codes) {
            const k = classifyKind(code);
            if (k) kindsSet.add(k);
          }
          const kinds = Array.from(kindsSet);
  
          Logger.log("%s\t%s\t[%s]", regionCode, kinds.join(","), codes.join(","));
          totalHits++;
          seenRegion.add(regionCode);
        }
  
        Utilities.sleep(FETCH_SLEEP_MS);
      }
    }
  
    Logger.log("--- SUMMARY ---");
    Logger.log("prefectures: %s, areas scanned: %s, active hits: %s", totalPref, totalAreas, totalHits);
    Logger.log("--- Collect JMA Warnings END: %s ---", new Date().toISOString());
  }
  
  /**
   * 都道府県コードに対して、取得対象となる warning JSON のソースコード一覧を返す（最終版）
   * - 通常: [prefCode]（例: 130000 → 130000.json）
   * - 6桁.json が 404 の場合:
   *   - prefCode === "010000"（北海道）:
   *       1) class15s で parent=010000 の子を列挙
   *       2) 各 class15 を parent とする class20s を列挙
   *       3) 得られた class20 コード群を返す
   *   - その他: area.offices から parent=prefCode の code 群を列挙
   */
  function listWarningSourcesForPref_Final_(prefCode, areaConst) {
    const src = [];
  
    // まず 6桁.json の存在確認
    const probe = tryFetch(`https://www.jma.go.jp/bosai/warning/data/warning/${prefCode}.json`, 0);
    if (probe && probe.getResponseCode() === 200) {
      src.push(prefCode);
      return src;
    }
  
    if (!areaConst) return src; // area.json が取れなければ何もできない
  
    if (prefCode === "010000") {
      // 1) 北海道は class15s を parent=010000 で探索
      const class15s = areaConst.class15s || {};
      const c15 = collectChildrenCodes_(class15s, prefCode);
  
      // 2) 各 class15 を親とする class20s を探索
      const class20s = areaConst.class20s || {};
      const c20 = [];
      for (const p of c15) {
        c20.push(...collectChildrenCodes_(class20s, p));
      }
  
      if (c20.length) {
        Logger.log("INFO: %s -> class15->class20 sources: %s", prefCode, c20.join(","));
        return c20;
      }
      Logger.log("WARN: no class15/class20 found under %s (area.json)", prefCode);
      return src;
    }
  
    // その他の都府県: offices を探索
    const offices = areaConst.offices || {};
    const officeCodes = collectChildrenCodes_(offices, prefCode);
    if (officeCodes.length) {
      Logger.log("INFO: %s -> office sources: %s", prefCode, officeCodes.join(","));
      return officeCodes;
    }
  
    return src;
  }
  
  /**
   * area.json の任意の辞書から parent が一致する子コードを列挙
   */
  function collectChildrenCodes_(dict, parentCode) {
    const out = [];
    for (const code in dict) {
      const node = dict[code];
      if (!node) continue;
      if (String(node.parent) === String(parentCode)) out.push(code);
    }
    return out;
  }
  
  /**
   * 任意のソースコード（6桁の prefecture / office / class20 コード）で warning JSON を取得
   */
  function fetchWarningJsonByCode_(code) {
    const url = `https://www.jma.go.jp/bosai/warning/data/warning/${code}.json`;
    let res = tryFetch(url, RETRY);
    if (!res || res.getResponseCode() !== 200) {
      const rc = res ? res.getResponseCode() : "(no response)";
      Logger.log("WARN: HTTP %s for %s", rc, url);
      return null;
    }
    try {
      return JSON.parse(res.getContentText("UTF-8"));
    } catch (e) {
      Logger.log("ERROR: JSON parse failed for %s : %s", code, e);
      return null;
    }
  }
  
  /**
   * area.json（区域定義）を取得
   */
  function fetchAreaConst_() {
    const res = tryFetch(AREA_CONST_URL, RETRY);
    if (!res || res.getResponseCode() !== 200) {
      const rc = res ? res.getResponseCode() : "(no response)";
      Logger.log("WARN: area.json HTTP %s", rc);
      return null;
    }
    try {
      return JSON.parse(res.getContentText("UTF-8"));
    } catch (e) {
      Logger.log("ERROR: area.json parse failed: %s", e);
      return null;
    }
  }
  
  /**
   * 軽量リトライ付きフェッチ
   */
  function tryFetch(url, retry) {
    for (let i = 0; i <= retry; i++) {
      try {
        const res = UrlFetchApp.fetch(url, { muteHttpExceptions: true, followRedirects: true });
        if (res.getResponseCode() >= 500 && i < retry) { // 5xx は再試行
          Utilities.sleep(250);
          continue;
        }
        return res;
      } catch (e) {
        if (i === retry) {
          Logger.log("ERROR: fetch failed (%s) : %s", url, e);
          return null;
        }
        Utilities.sleep(250);
      }
    }
    return null;
  }
  
  /**
   * 警報コードの先頭桁で上位種別を判定
   *  - 3* → 特別警報
   *  - 0* → 警報
   *  - 1*,2* → 注意報
   */
  function classifyKind(code) {
    if (!code) return "";
    const head = code.charAt(0);
    if (head === "3") return "特別警報";
    if (head === "0") return "警報";
    if (head === "1" || head === "2") return "注意報";
    return "";
  }
  