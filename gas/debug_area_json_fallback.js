/**
 * area.json 修正が効果なしの原因調査
 */

function debug_area_json_fallback_issue() {
  console.log('=== area.json修正が効果なしの原因調査 ===');
  
  console.log('【1. area.json取得テスト】');
  try {
    const areaRes = UrlFetchApp.fetch('https://www.jma.go.jp/bosai/common/const/area.json');
    const areaData = JSON.parse(areaRes.getContentText());
    
    console.log('✅ area.json取得成功');
    console.log(`データサイズ: ${areaRes.getContentText().length} 文字`);
    
    // class15s の確認
    const class15s = areaData.class15s || {};
    console.log(`class15s存在: ${Object.keys(class15s).length}件`);
    
    // 北海道の子要素確認
    const hokkaidoChildren = [];
    for (const code in class15s) {
      const node = class15s[code];
      if (node && String(node.parent) === '010000') {
        hokkaidoChildren.push({
          code: code,
          name: node.name
        });
      }
    }
    
    console.log(`北海道の支庁 (class15s): ${hokkaidoChildren.length}件`);
    hokkaidoChildren.forEach(child => {
      console.log(`  ${child.code}: ${child.name}`);
    });
    
    // 017000が含まれているかチェック
    const has017 = hokkaidoChildren.some(c => c.code === '017000');
    console.log(`017000の存在: ${has017 ? '✅' : '❌'}`);
    
    if (has017) {
      console.log('→ area.jsonには017000が含まれている');
      console.log('→ 修正したロジックが機能していない');
    } else {
      console.log('→ area.jsonに017000がない');
      console.log('→ JMA APIとarea.jsonの不整合');
    }
    
  } catch (e) {
    console.log('❌ area.json取得失敗:', e);
    console.log('→ フォールバックのハードコードが作動');
  }
}

function debug_modified_logic_execution() {
  console.log('\n=== 修正したロジックの実行確認 ===');
  
  // 修正したロジックを直接テスト
  console.log('【修正版ロジックのステップ実行】');
  
  const pref = '010000';
  console.log(`1. 対象県コード: ${pref}`);
  
  // area.json 取得テスト
  let areaConst = null;
  try {
    const areaRes = UrlFetchApp.fetch('https://www.jma.go.jp/bosai/common/const/area.json');
    areaConst = JSON.parse(areaRes.getContentText());
    console.log('2. area.json取得: ✅');
  } catch (e) {
    console.log('2. area.json取得: ❌', e);
    console.log('→ ここでフォールバックに入る');
    return;
  }
  
  // class15s 展開
  const class15s = areaConst.class15s || {};
  const c15 = [];
  for (const code in class15s) {
    const node = class15s[code];
    if (node && String(node.parent) === pref) {
      c15.push(code);
    }
  }
  
  console.log(`3. class15s展開: ${c15.length}件`);
  console.log(`   コード: [${c15.join(', ')}]`);
  
  // class20s 展開
  const class20s = areaConst.class20s || {};
  const c20 = [];
  for (const parent of c15) {
    for (const code in class20s) {
      const node = class20s[code];
      if (node && String(node.parent) === parent) {
        c20.push(code);
      }
    }
  }
  
  console.log(`4. class20s展開: ${c20.length}件`);
  console.log(`   最初の10件: [${c20.slice(0, 10).join(', ')}]`);
  
  // 帯広市の確認
  const hasObihiro = c20.some(code => code.includes('168'));
  console.log(`5. 帯広市含有: ${hasObihiro ? '✅' : '❌'}`);
  
  if (c20.length === 0) {
    console.log('→ class20s展開失敗、class15sを返すべき');
    console.log(`   class15s: [${c15.join(', ')}]`);
    
    if (c15.length === 0) {
      console.log('→ class15s展開も失敗、フォールバックへ');
    }
  }
}

function debug_017000_detailed() {
  console.log('\n=== 017000支庁の詳細調査 ===');
  
  try {
    const res = UrlFetchApp.fetch('https://www.jma.go.jp/bosai/warning/data/warning/017000.json');
    const json = JSON.parse(res.getContentText());
    
    console.log('017000 API応答成功');
    
    // areaTypes[0] の詳細
    const areas0 = json?.areaTypes?.[0]?.areas || [];
    console.log(`areaTypes[0]: ${areas0.length}エリア`);
    areas0.forEach((area, i) => {
      console.log(`  [${i}] ${area.code}: ${area.name || 'N/A'}`);
    });
    
    // areaTypes[1] の詳細（先頭5件のみ）
    const areas1 = json?.areaTypes?.[1]?.areas || [];
    console.log(`\nareaTypes[1]: ${areas1.length}エリア（先頭5件）`);
    areas1.slice(0, 5).forEach((area, i) => {
      console.log(`  [${i}] ${area.code}: ${area.name || 'N/A'}`);
    });
    
    // 帯広市や十勝系の市町村を探す
    const tokachiAreas = areas1.filter(area => {
      const name = area.name || '';
      return name.includes('帯広') || name.includes('音更') || 
             name.includes('芽室') || name.includes('清水') ||
             area.code.includes('168');
    });
    
    console.log(`\n十勝系市町村: ${tokachiAreas.length}件`);
    tokachiAreas.forEach(area => {
      console.log(`  🎯 ${area.code}: ${area.name}`);
    });
    
    if (tokachiAreas.length > 0) {
      console.log('✅ 017000が十勝地方の正しい支庁コード！');
    }
    
  } catch (e) {
    console.log('017000 API応答失敗:', e);
  }
}

function debug_why_hardcode_returned() {
  console.log('\n=== なぜハードコードが返されたのか ===');
  
  console.log('【可能性1】area.json取得でエラー');
  console.log('【可能性2】class15s/class20s展開で空配列');
  console.log('【可能性3】修正したコードが実際には実行されていない');
  
  // 実際にlistWarningSourcesForPref_を呼んでエラー箇所を特定
  console.log('\n実際の関数実行でのエラー確認:');
  
  try {
    const result = listWarningSourcesForPref_('010000', null);
    console.log(`結果: [${result.join(', ')}]`);
    console.log('→ 関数自体は正常実行');
    
    if (result.length === 5 && result.includes('016000')) {
      console.log('→ フォールバックのハードコードが実行された');
    }
    
  } catch (e) {
    console.log('関数実行エラー:', e);
  }
}

