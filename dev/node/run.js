'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const { createGasMocks } = require('./gas-mocks');
const { prefetchAll } = require('./prefetch');

async function main() {
  const { prefetched } = await prefetchAll();
  const mocks = createGasMocks(prefetched);

  const sandbox = {
    console,
    JSON,
    Date,
    Map,
    Set,
    Array,
    Object,
    String,
    Number,
    Boolean,
    RegExp,
    Math,
    ...mocks
  };
  const context = vm.createContext(sandbox);

  for (const fname of ['Code.js', 'Points.js']) {
    const src = fs.readFileSync(path.join(__dirname, '..', '..', 'gas', fname), 'utf8');
    vm.runInContext(src, context, { filename: fname });
  }

  const args = {
    levels: ['tokubetsu', 'kiken', 'keihou', 'chuui'],
    phenomena: []
  };
  const result = context.getActiveWarningFeatures(args);

  console.log('\n=== 結果サマリー ===');
  console.log('features:', result.features.length);
  console.log('unresolved:', result.unresolved.length);

  const byLevel = {};
  for (const f of result.features) {
    const lv = f.properties.level;
    byLevel[lv] = (byLevel[lv] || 0) + 1;
  }
  console.log('レベル別件数:', byLevel);

  const reasonCount = {};
  for (const u of result.unresolved) {
    reasonCount[u.reason] = (reasonCount[u.reason] || 0) + 1;
  }
  console.log('unresolved理由別件数:', reasonCount);

  console.log('\nunresolvedサンプル(先頭20件):');
  console.log(result.unresolved.slice(0, 20));

  console.log('\nfeaturesサンプル(先頭5件のproperties):');
  console.log(result.features.slice(0, 5).map(f => f.properties));
}

main().catch(e => {
  console.error(e);
  process.exit(1);
});
