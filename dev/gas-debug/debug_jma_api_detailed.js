/**
 * JMA API の詳細調査スクリプト
 * 公式サイトで表示されているのに取得できない原因を特定
 */

function debug_jma_api_detailed() {
  console.log('=== JMA API 詳細調査 ===');
  
  // 1) 北海道支庁別の詳細確認
  const hokkaidoSources = ['011000', '012000', '013000', '015000', '016000'];
  
  for (const srcCode of hokkaidoSources) {
    console.log(`\n--- 支庁 ${srcCode} の詳細 ---`);
    
    try {
      const url = `https://www.jma.go.jp/bosai/warning/data/warning/${srcCode}.json`;
      const res = UrlFetchApp.fetch(url, { muteHttpExceptions: true });
      const status = res.getResponseCode();
      
      if (status !== 200) {
        console.log(`HTTP ${status}: データなし`);
        continue;
      }
      
      const json = JSON.parse(res.getContentText('UTF-8'));
      const areas = json?.areaTypes?.[1]?.areas || [];
      
      console.log(`エリア数: ${areas.length}`);
      
      // 各エリアの警報状況を詳細確認
      let fogCount = 0;
      for (const area of areas) {
        const warnings = area.warnings || [];
        const activeWarnings = warnings.filter(w => 
          ['発表','継続','切替'].includes(String(w.status || '').trim())
        );
        
        if (activeWarnings.length > 0) {
          const codes = activeWarnings.map(w => String(w.code || '').trim());
          const hasFog = codes.some(c => c === '20' || parseInt(c) === 20);
          
          if (hasFog) {
            fogCount++;
            console.log(`  📍 ${area.code}: 濃霧注意報発令中`);
            console.log(`    codes: [${codes.join(',')}]`);
            console.log(`    warnings:`, activeWarnings.map(w => `${w.code}:${w.status}`));
          }
        }
      }
      
      if (fogCount === 0) {
        console.log(`  濃霧注意報: なし`);
      } else {
        console.log(`  📈 濃霧注意報: ${fogCount} エリア`);
      }
      
    } catch (e) {
      console.log(`エラー: ${e}`);
    }
  }
  
  // 2) 原APIのタイムスタンプ確認
  console.log('\n--- API タイムスタンプ確認 ---');
  try {
    const timestampUrl = 'https://www.jma.go.jp/bosai/warning/data/warning/011000.json';
    const res = UrlFetchApp.fetch(timestampUrl);
    const json = JSON.parse(res.getContentText());
    
    console.log(`データ更新時刻: ${json.reportDatetime || 'N/A'}`);
    console.log(`取得時刻: ${new Date().toISOString()}`);
  } catch (e) {
    console.log(`タイムスタンプ取得エラー: ${e}`);
  }
}

function debug_cache_issue() {
  console.log('=== キャッシュ問題の調査 ===');
  
  // Script Cache の状況確認
  const cache = CacheService.getScriptCache();
  const hokkaidoSources = ['011000', '012000', '013000', '015000', '016000'];
  
  for (const src of hokkaidoSources) {
    const key = 'WARN_' + src;
    const cached = cache.get(key);
    
    if (cached) {
      try {
        const data = JSON.parse(cached);
        const areas = data?.areaTypes?.[1]?.areas || [];
        const fogAreas = areas.filter(area => {
          const warnings = area.warnings || [];
          const activeWarnings = warnings.filter(w => 
            ['発表','継続','切替'].includes(String(w.status || '').trim())
          );
          return activeWarnings.some(w => String(w.code || '').trim() === '20');
        });
        
        console.log(`${src}: キャッシュあり (エリア${areas.length}, 濃霧${fogAreas.length})`);
      } catch (e) {
        console.log(`${src}: キャッシュ破損`);
      }
    } else {
      console.log(`${src}: キャッシュなし`);
    }
  }
  
  // キャッシュクリア実行
  console.log('\n⚠️ 北海道関連キャッシュをクリアします');
  for (const src of hokkaidoSources) {
    cache.remove('WARN_' + src);
  }
  console.log('✅ キャッシュクリア完了');
}

function debug_normalized_codes() {
  console.log('=== コード正規化の詳細確認 ===');
  
  // 様々な形式のコード20をテスト
  const testValues = [
    20, '20', ' 20', '20 ', '\t20\n',
    '020', 'code20', {code: 20}, 
    null, undefined, '', 0
  ];
  
  testValues.forEach(val => {
    try {
      const normalized = normalizeR06Code_(val);
      console.log(`${typeof val} ${JSON.stringify(val)} -> '${normalized}'`);
    } catch (e) {
      console.log(`${typeof val} ${JSON.stringify(val)} -> エラー: ${e}`);
    }
  });
}
