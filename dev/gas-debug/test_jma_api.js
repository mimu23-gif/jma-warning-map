/**
 * JMA API の北海道・沖縄確認用スクリプト
 * Apps Script エディタで実行してください
 */

function test_jma_hokkaido_okinawa() {
  console.log('=== JMA API 北海道・沖縄テスト ===');
  
  // 1) 都道府県レベルでの直接アクセステスト
  const prefTests = [
    { name: '北海道', code: '010000' },
    { name: '沖縄', code: '470000' }
  ];
  
  prefTests.forEach(test => {
    const url = `https://www.jma.go.jp/bosai/warning/data/warning/${test.code}.json`;
    try {
      const res = UrlFetchApp.fetch(url, { muteHttpExceptions: true });
      const status = res.getResponseCode();
      console.log(`${test.name} (${test.code}): HTTP ${status}`);
      
      if (status === 200) {
        const json = JSON.parse(res.getContentText());
        const areas = json?.areaTypes?.[1]?.areas || [];
        console.log(`  -> areas: ${areas.length} 件`);
        if (areas.length > 0) {
          console.log(`  -> 先頭エリア: ${areas[0].code} ${areas[0].name || ''}`);
        }
      }
    } catch (e) {
      console.log(`${test.name} (${test.code}): ERROR ${e}`);
    }
  });
  
  // 2) 支庁・地域レベルでのテスト
  console.log('\n--- 細分化コードテスト ---');
  
  // 北海道支庁（推定）
  const hokkaidoSubs = ['011000', '012000', '013000', '014000', '015000', '016000'];
  hokkaidoSubs.forEach(code => {
    testSubCode('北海道支庁', code);
  });
  
  // 沖縄地域（推定）
  const okinawaSubs = ['471000', '472000', '473000', '474000'];
  okinawaSubs.forEach(code => {
    testSubCode('沖縄地域', code);
  });
  
  // 3) area.json からの構造確認
  console.log('\n--- area.json 構造確認 ---');
  try {
    const areaRes = UrlFetchApp.fetch('https://www.jma.go.jp/bosai/common/const/area.json');
    if (areaRes.getResponseCode() === 200) {
      const areaJson = JSON.parse(areaRes.getContentText());
      
      // 北海道の構造
      const hokkaido = areaJson?.offices?.['010000'];
      if (hokkaido) {
        console.log('北海道 office 構造:');
        console.log(`  name: ${hokkaido.name || 'N/A'}`);
        console.log(`  children: ${hokkaido.children ? hokkaido.children.length : 0} 件`);
        if (hokkaido.children) {
          console.log(`  子コード例: ${hokkaido.children.slice(0, 5).join(', ')}`);
        }
      }
      
      // 沖縄の構造
      const okinawa = areaJson?.offices?.['470000'];
      if (okinawa) {
        console.log('沖縄 office 構造:');
        console.log(`  name: ${okinawa.name || 'N/A'}`);
        console.log(`  children: ${okinawa.children ? okinawa.children.length : 0} 件`);
        if (okinawa.children) {
          console.log(`  子コード例: ${okinawa.children.slice(0, 5).join(', ')}`);
        }
      }
    }
  } catch (e) {
    console.log(`area.json 取得エラー: ${e}`);
  }
}

function testSubCode(region, code) {
  const url = `https://www.jma.go.jp/bosai/warning/data/warning/${code}.json`;
  try {
    const res = UrlFetchApp.fetch(url, { muteHttpExceptions: true });
    const status = res.getResponseCode();
    if (status === 200) {
      const json = JSON.parse(res.getContentText());
      const areas = json?.areaTypes?.[1]?.areas || [];
      if (areas.length > 0) {
        console.log(`${region} ${code}: ✅ ${areas.length} areas`);
        return;
      }
    }
    console.log(`${region} ${code}: ❌ HTTP ${status} or no areas`);
  } catch (e) {
    console.log(`${region} ${code}: ❌ ERROR`);
  }
}
