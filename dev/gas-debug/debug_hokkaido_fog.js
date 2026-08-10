/**
 * 北海道濃霧注意報の特定デバッグ
 * Apps Script で実行
 * 
 * 注意: このスクリプトは両階層監視（areaTypes[0,1]）修正後の動作確認用
 */

function debug_hokkaido_fog_issue() {
  console.log('=== 北海道濃霧注意報デバッグ ===');
  
  // 1) 北海道の警報データを詳細確認
  const warnings = getActiveWarnings({ levels: ['tokubetsu','kiken','keihou','chuui'], phenomena: [] });
  const hokkaidoWarnings = warnings.items.filter(item => item.regionCode.startsWith('01'));
  
  console.log(`北海道警報総数: ${hokkaidoWarnings.length}`);
  
  // 濃霧関連のみを抽出
  const fogWarnings = hokkaidoWarnings.filter(item => 
    item.codes.includes('20') || item.kinds.includes('濃霧')
  );
  
  console.log(`北海道濃霧警報: ${fogWarnings.length} 件`);
  fogWarnings.forEach(w => {
    console.log(`  ${w.regionCode}: level=${w.level} codes=[${w.codes.join(',')}] kinds=[${w.kinds.join(',')}]`);
  });
  
  // 2) 北海道警報のコード分布確認
  const allCodes = new Set();
  hokkaidoWarnings.forEach(w => w.codes.forEach(c => allCodes.add(c)));
  console.log(`\n北海道で発令中のコード: [${Array.from(allCodes).sort().join(',')}]`);
  
  // 3) getActiveWarningFeatures での北海道濃霧の処理確認
  console.log('\n--- FeatureCollection 変換結果 ---');
  const features = getActiveWarningFeatures({ levels: ['tokubetsu','kiken','keihou','chuui'], phenomena: [] });
  const hokkaidoFeatures = features.features.filter(f => f.properties.code.startsWith('01'));
  
  console.log(`北海道フィーチャ総数: ${hokkaidoFeatures.length}`);
  
  const fogFeatures = hokkaidoFeatures.filter(f => 
    f.properties.codes.includes('20') || f.properties.kinds.includes('濃霧')
  );
  
  console.log(`北海道濃霧フィーチャ: ${fogFeatures.length} 件`);
  fogFeatures.forEach(f => {
    const props = f.properties;
    console.log(`  ${props.code}: ${props.name} level=${props.level} codes=[${props.codes.join(',')}]`);
  });
  
  // 4) 濃霧警報が出ているのにフィーチャが作られない場合の詳細調査
  if (fogWarnings.length > 0 && fogFeatures.length === 0) {
    console.log('\n⚠️ 濃霧警報はあるがフィーチャなし - 詳細調査:');
    
    for (const fogWarn of fogWarnings) {
      console.log(`\n調査対象: ${fogWarn.regionCode}`);
      
      // インデックス解決確認
      const index = loadRegionIndex_();
      const pick = findIndexEntryForCode_(index, fogWarn.regionCode, false);
      if (!pick) {
        console.log(`  ❌ インデックス未解決`);
        continue;
      }
      console.log(`  ✅ インデックス解決: fileId=${pick.fileId} raw7=${pick.raw7}`);
      
      // GeoJSON読み取り確認
      try {
        const feature = readSingleFeature_(pick.fileId, pick.raw7);
        if (!feature) {
          console.log(`  ❌ GeoJSONフィーチャ未発見`);
        } else {
          console.log(`  ✅ GeoJSONフィーチャ発見: ${feature.properties?.name || 'N/A'}`);
        }
      } catch (e) {
        console.log(`  ❌ GeoJSON読み取りエラー: ${e}`);
      }
    }
  }
  
  // 5) 未解決コードの確認
  const unresolved = features.unresolved || [];
  const hokkaidoUnresolved = unresolved.filter(u => u.code.startsWith('01'));
  if (hokkaidoUnresolved.length > 0) {
    console.log(`\n北海道未解決コード: ${hokkaidoUnresolved.length} 件`);
    hokkaidoUnresolved.forEach(u => {
      console.log(`  ${u.code}: ${u.reason}`);
    });
  }
}

function debug_fog_specific_codes() {
  console.log('=== 濃霧コード20の詳細確認 ===');
  
  // normalizeR06Code_ の動作確認
  const testCodes = [20, '20', ' 20 ', '020'];
  testCodes.forEach(code => {
    const normalized = normalizeR06Code_(code);
    console.log(`${typeof code} ${code} -> '${normalized}'`);
  });
  
  // highestLevel_ での濃霧分類確認
  const level = highestLevel_(['20']);
  console.log(`コード['20']のレベル判定: ${level}`);
  
  // codeToKindName_ での濃霧名称確認
  const kindName = codeToKindName_('20');
  console.log(`コード'20'の現象名: ${kindName}`);
}
