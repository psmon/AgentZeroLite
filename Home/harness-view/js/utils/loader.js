/** fetch 래퍼 - 텍스트/JSON 로드와 에러 처리 */

export async function fetchText(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`fetch ${url} -> ${res.status}`);
  return await res.text();
}

export async function fetchJson(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`fetch ${url} -> ${res.status}`);
  return await res.json();
}

/** 인덱스 파일 로드 (harness-view/indexes/*.json) */
export async function loadIndex(name) {
  try {
    return await fetchJson(`indexes/${name}.json`);
  } catch (e) {
    console.warn(`[loadIndex] ${name}: ${e.message}`);
    return null;
  }
}

/** 정적 data (사전구현) 로드 (harness-view/data/*.json) */
export async function loadData(name) {
  try {
    return await fetchJson(`data/${name}.json`);
  } catch (e) {
    console.warn(`[loadData] ${name}: ${e.message}`);
    return null;
  }
}

/** Resource (MD) loader.
 *  External resources (Docs/, harness/) are mirrored into Home/_resources/
 *  by build-indexes.js so GitHub Pages (which uploads only Home/) can serve
 *  them. From Home/harness-view/, `../_resources/<rel>` reaches the mirror
 *  in both local dev and Pages runs. */
export async function loadMd(relativePath) {
  try {
    return await fetchText(`../_resources/${relativePath}`);
  } catch (e) {
    console.warn(`[loadMd] ${relativePath}: ${e.message}`);
    return null;
  }
}
