'use strict';

const WARNING_MAP_R8_URL = 'https://www.jma.go.jp/bosai/warning/data/r8/map.json';
const AREA_CONST_URL = 'https://www.jma.go.jp/bosai/common/const/area.json';

async function fetchOne(url) {
  try {
    const res = await fetch(url);
    const body = await res.text();
    return { status: res.status, body };
  } catch (e) {
    return { status: 0, body: '' };
  }
}

/** 実際のJMA APIを叩いて全国フィード（r8/map.json）とarea.jsonを事前取得し、URL->レスポンスのMapを返す */
async function prefetchAll() {
  const prefetched = new Map();

  for (const url of [WARNING_MAP_R8_URL, AREA_CONST_URL]) {
    const res = await fetchOne(url);
    prefetched.set(url, res);
    if (res.status !== 200) console.error(`[prefetch] WARN HTTP ${res.status}: ${url}`);
    else console.error(`[prefetch] OK (${res.body.length} bytes): ${url}`);
  }

  return { prefetched };
}

module.exports = { prefetchAll, WARNING_MAP_R8_URL, AREA_CONST_URL };
