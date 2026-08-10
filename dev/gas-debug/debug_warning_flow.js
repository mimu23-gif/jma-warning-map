/**
 * 警報取得〜描画フローのデバッグスクリプト
 * Apps Script で実行してください
 */

function debug_warning_flow() {
  console.log('=== 警報取得〜描画フローデバッグ ===');
  
  // 1) getActiveWarnings の結果確認
  console.log('\n--- Step 1: getActiveWarnings ---');
  const warningsResult = getActiveWarnings({
    levels: ['tokubetsu','kiken','keihou','chuui'],
    phenomena: []
  });
  
  console.log(`総警報件数: ${warningsResult.items.length}`);
  
  // 北海道・沖縄・東京（八丈島）の件数確認
  const hokkaidoItems = warningsResult.items.filter(item => item.regionCode.startsWith('01'));
  const okinawaItems = warningsResult.items.filter(item => item.regionCode.startsWith('47'));
  const tokyoItems = warningsResult.items.filter(item => item.regionCode.startsWith('13'));
  
  console.log(`北海道警報: ${hokkaidoItems.length} 件`);
  console.log(`沖縄警報: ${okinawaItems.length} 件`);
  console.log(`東京警報: ${tokyoItems.length} 件`);
  
  if (hokkaidoItems.length > 0) {
    console.log('北海道例:', hokkaidoItems[0].regionCode, hokkaidoItems[0].level, hokkaidoItems[0].kinds);
  }
  if (okinawaItems.length > 0) {
    console.log('沖縄例:', okinawaItems[0].regionCode, okinawaItems[0].level, okinawaItems[0].kinds);
  }
  if (tokyoItems.length > 0) {
    console.log('東京例:', tokyoItems[0].regionCode, tokyoItems[0].level, tokyoItems[0].kinds);
  }
  
  // 2) getActiveWarningFeatures の結果確認
  console.log('\n--- Step 2: getActiveWarningFeatures ---');
  const featuresResult = getActiveWarningFeatures({
    levels: ['tokubetsu','kiken','keihou','chuui'],
    phenomena: []
  });
  
  console.log(`描画可能フィーチャ: ${featuresResult.features.length}`);
  console.log(`未解決コード: ${featuresResult.unresolved.length}`);
  
  // 北海道・沖縄・東京の描画可能性確認
  const hokkaidoFeatures = featuresResult.features.filter(f => f.properties.code.startsWith('01'));
  const okinawaFeatures = featuresResult.features.filter(f => f.properties.code.startsWith('47'));
  const tokyoFeatures = featuresResult.features.filter(f => f.properties.code.startsWith('13'));
  
  console.log(`北海道フィーチャ: ${hokkaidoFeatures.length}`);
  console.log(`沖縄フィーチャ: ${okinawaFeatures.length}`);
  console.log(`東京フィーチャ: ${tokyoFeatures.length}`);
  
  // 未解決の詳細
  const hokkaidoUnresolved = featuresResult.unresolved.filter(u => u.code.startsWith('01'));
  const okinawaUnresolved = featuresResult.unresolved.filter(u => u.code.startsWith('47'));
  
  if (hokkaidoUnresolved.length > 0) {
    console.log('北海道未解決:', hokkaidoUnresolved.slice(0, 5));
  }
  if (okinawaUnresolved.length > 0) {
    console.log('沖縄未解決:', okinawaUnresolved.slice(0, 5));
  }
  
  // 3) 特定コードのインデックス解決テスト
  console.log('\n--- Step 3: インデックス解決テスト ---');
  const index = loadRegionIndex_();
  
  // 北海道の支庁コードがインデックスで解決できるかテスト
  const hokkaidoSubCodes = ['011000', '012000', '013000', '015000', '016000'];
  hokkaidoSubCodes.forEach(code => {
    const pick = findIndexEntryForCode_(index, code, false);
    console.log(`${code}: ${pick ? '✅ 解決可能' : '❌ 未解決'} ${pick ? 'fileId=' + pick.fileId : ''}`);
  });
  
  // 沖縄の地域コードテスト
  const okinawaSubCodes = ['471000', '472000', '473000', '474000'];
  okinawaSubCodes.forEach(code => {
    const pick = findIndexEntryForCode_(index, code, false);
    console.log(`${code}: ${pick ? '✅ 解決可能' : '❌ 未解決'} ${pick ? 'fileId=' + pick.fileId : ''}`);
  });
  
  // 4) 八丈島コードの確認（なぜ警報なしでも描画されるか）
  console.log('\n--- Step 4: 八丈島コード確認 ---');
  // 八丈島周辺のコード（推定）
  const hachijojiCodes = ['1322100', '1322200']; // 八丈島町、青ヶ島村
  hachijojiCodes.forEach(code => {
    const pick = findIndexEntryForCode_(index, code, false);
    if (pick) {
      console.log(`八丈島 ${code}: インデックス解決可能 fileId=${pick.fileId}`);
      // 実際の警報状況確認
      const warningItem = warningsResult.items.find(item => item.regionCode === code);
      console.log(`  警報状況: ${warningItem ? '発令中' : '発令なし'}`);
    }
  });
}

function debug_specific_codes() {
  console.log('=== 特定コードの詳細確認 ===');
  
  // 実際に取得された警報コードのサンプル確認
  const warnings = getActiveWarnings({levels: ['tokubetsu','kiken','keihou','chuui'], phenomena: []});
  const allCodes = warnings.items.map(item => item.regionCode);
  
  console.log('全警報コード例（最初20件）:');
  allCodes.slice(0, 20).forEach(code => console.log(`  ${code}`));
  
  // 北海道・沖縄で実際に警報が出ているコードを特定
  const hCodes = allCodes.filter(c => c.startsWith('01'));
  const oCodes = allCodes.filter(c => c.startsWith('47'));
  
  console.log(`\n北海道で警報発令中のコード: ${hCodes.length}件`);
  hCodes.forEach(code => console.log(`  ${code}`));
  
  console.log(`\n沖縄で警報発令中のコード: ${oCodes.length}件`);
  oCodes.forEach(code => console.log(`  ${code}`));
}
