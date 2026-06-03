// 스킬을 새 환경에 복사한 직후 동작 확인용.
// 사용법:
//   1) 작업 디렉토리에서 `npm i -D playwright` (또는 전역 설치 환경에서 그대로)
//   2) node <skill-path>/scripts/smoke.mjs
// 통과 기준: TITLE/UA 출력, EDGE_OK가 true.

const tryImport = async () => {
  try {
    return await import('playwright');
  } catch {
    // 전역 설치 fallback
    const { execSync } = await import('node:child_process');
    const root = execSync('npm root -g').toString().trim().replace(/\\/g, '/');
    return import(`file:///${root}/playwright/index.mjs`);
  }
};

const { chromium } = await tryImport();

let browser;
try {
  browser = await chromium.launch({ channel: 'msedge', headless: true });
} catch (e) {
  console.warn('Edge 채널 실패 — 기본 chromium으로 fallback:', e.message);
  browser = await chromium.launch({ headless: true });
}

const page = await browser.newPage();
await page.goto('https://example.com', { waitUntil: 'domcontentloaded' });
const title = await page.title();
const ua = await page.evaluate(() => navigator.userAgent);
console.log('TITLE   :', title);
console.log('UA      :', ua);
console.log('EDGE_OK :', ua.includes('Edg/'));
await browser.close();
