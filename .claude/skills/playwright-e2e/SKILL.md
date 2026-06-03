---
name: playwright-e2e
description: Playwright를 이용한 브라우저 e2e/UI 자동화. 로그인·회원가입·폼 제출·SPA 라우팅·네트워크 모킹·시각 회귀·스크린샷·PDF 캡처·인증 상태 저장·다중 탭 시나리오 등 실제 브라우저가 필요한 모든 작업에 발동. "e2e 테스트", "브라우저 자동화", "Playwright", "codegen", "셀렉터 잡아줘", "로그인 플로우 자동화", "페이지 스크린샷 자동화", "UI 회귀 테스트", "headless로 돌려봐", "Edge에서 ~ 동작 확인" 같은 단어가 보이면 즉시 발동. 기본 채널은 **Microsoft Edge** — 사용자가 일상 브라우징에 쓰는 Chrome과 자동화 세션을 섞지 않기 위함. fetch/HTTP만으로 충분한 단순 API 검증, BeautifulSoup/cheerio로 끝나는 정적 HTML 파싱, requests 한 줄짜리 다운로드에는 사용하지 말 것.
---

# Playwright e2e (Edge 채널 기본)

이 스킬은 실제 브라우저가 필요한 자동화·테스트 작업을 Playwright로 수행할 때 발동한다. 어떤 환경에 복사해 놓아도 동작하도록 self-contained로 작성됐다.

## 1. 첫 번째 원칙: 왜 Edge 채널인가

이 스킬을 채택한 환경은 보통 **Chrome은 사람이 쓰는 브라우저, Edge는 자동화 전용**으로 분리하는 정책을 쓴다. 이유:

- 로그인 세션·확장·북마크·페이지 캐시가 일상 사용과 섞이면 디버깅이 지옥이 된다.
- 자동화가 사용자 프로필을 잠그거나 쿠키를 덮어쓰는 사고를 방지한다.
- Playwright의 기본 chromium 빌드가 아니라 **시스템에 설치된 Edge**를 사용하므로 브라우저 다운로드 용량(~수백 MB)도 절약된다.

따라서 모든 코드 예시는 `channel: 'msedge'` 를 명시한다. 사용자가 명시적으로 "크롬으로", "Firefox로", "webkit으로" 요청한 경우에만 채널을 바꾼다. 환경에 Edge가 없으면 §6 의 fallback 절차로 간다.

## 2. 사전 확인 — 환경 점검을 먼저

작업 시작 전에 한 번에 확인하라. 빠진 게 있으면 §6 의 설치 절차로.

```powershell
node --version                                                     # v18+ 필요
playwright --version                                               # 없으면 §6 으로
Test-Path "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"  # Edge 존재
```

Linux/macOS면 `which node && which playwright && which microsoft-edge` (또는 `/Applications/Microsoft Edge.app`).

## 3. 두 가지 사용 모드

사용자의 요청 성격에 따라 하나를 고른다. 헷갈리면 사용자에게 물어보지 말고 아래 기준으로 판단:

| 상황 | 모드 |
|---|---|
| "이 페이지 한번 띄워서 ~ 확인해줘" / 일회성 스크립트 / 데이터 수집 | **A. ad-hoc 스크립트** |
| "이 플로우를 회귀 테스트로 만들어줘" / 여러 케이스 / CI 연동 | **B. @playwright/test 프로젝트** |
| 사용자가 셀렉터·플로우를 잘 모름 | **C. codegen 으로 녹화 먼저** |

### A. ad-hoc 스크립트 (`.mjs` 한 장)

```javascript
// run-edge.mjs
import { chromium } from 'playwright';

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

await page.goto('https://example.com', { waitUntil: 'domcontentloaded' });
await page.screenshot({ path: 'out.png', fullPage: true });

await browser.close();
```

실행: `node run-edge.mjs`. 디버깅 중에는 `headless: false, slowMo: 200` 으로 바꿔 눈으로 확인.

**전역 설치 환경의 ESM import 주의:** Playwright가 `npm i -g`로 전역 설치돼 있으면 `import 'playwright'` 의 모듈 해석이 실패할 수 있다. 두 가지 해결책:

1. **권장:** 작업 디렉토리에서 로컬 설치 — `npm init -y && npm i -D playwright @playwright/test` 후 `import 'playwright'` 그대로 사용.
2. **빠른 우회:** 절대경로 import. Windows 전역 설치라면
   ```javascript
   import { chromium } from 'file:///C:/Users/<USER>/AppData/Roaming/npm/node_modules/playwright/index.mjs';
   ```
   경로는 `npm root -g` 로 확인. CLI(`playwright codegen ...`)는 영향 없음.

### B. @playwright/test 프로젝트

회귀 테스트, CI, 다중 케이스라면 처음부터 이 모드로 간다.

```powershell
npm init -y
npm i -D @playwright/test
npx playwright install msedge   # "already installed" 메시지는 정상
```

`playwright.config.ts`:

```typescript
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  retries: process.env.CI ? 2 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    channel: 'msedge',                 // ← 모든 테스트의 기본 브라우저
    baseURL: process.env.BASE_URL ?? 'http://localhost:3000',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'edge-desktop', use: { ...devices['Desktop Edge'], channel: 'msedge' } },
    // 모바일이 필요하면 추가:
    // { name: 'edge-mobile', use: { ...devices['Pixel 7'], channel: 'msedge' } },
  ],
});
```

`tests/login.spec.ts` 예:

```typescript
import { test, expect } from '@playwright/test';

test('로그인 후 대시보드 진입', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill('user@example.com');
  await page.getByLabel('Password').fill(process.env.TEST_PW!);
  await page.getByRole('button', { name: '로그인' }).click();
  await expect(page).toHaveURL(/\/dashboard/);
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible();
});
```

실행: `npx playwright test` / 헤디드: `npx playwright test --headed` / 단일: `npx playwright test login`.

### C. codegen — 사용자가 셀렉터를 모를 때

```powershell
playwright codegen --channel=msedge https://target.example.com
```

Edge 창이 뜨고, 사용자가 클릭·입력하는 동작이 실시간으로 코드로 변환된다. 결과를 받아 §A 또는 §B 템플릿에 옮겨 넣고 셀렉터·assertion을 정리해 준다. **codegen 출력을 그대로 복붙하지 말 것** — 보통 `getByText('홈')` 같이 i18n에 약하거나, nth-child 같은 깨지기 쉬운 셀렉터가 섞여 있다. §4 의 locator 우선순위로 다듬는다.

## 4. Locator 모범 사례 (이 순서로 시도하라)

테스트가 깨지는 1번 원인은 셀렉터다. 위에서부터 시도하고 **위 단계가 가능하면 절대 아래로 내려가지 말 것**:

1. **역할 기반:** `page.getByRole('button', { name: '저장' })` — 접근성 트리 기준이라 가장 견고.
2. **라벨/플레이스홀더/텍스트:** `getByLabel`, `getByPlaceholder`, `getByText` — 사용자에게 보이는 정보 기준.
3. **테스트 id:** `getByTestId('submit-btn')` — 1·2 가 불가능하면 개발자에게 `data-testid` 추가를 요청.
4. **CSS/XPath:** 최후의 수단. nth-child, 긴 클래스 체인은 깨지기 쉽다.

`waitForSelector` / 임의 `setTimeout` 대신 `expect(...).toBeVisible()` 같은 **auto-retry assertion**을 쓴다. Playwright는 이미 retry/대기 로직이 내장돼 있고, 명시적 sleep은 flaky 테스트의 원흉이다.

더 깊은 패턴(인증 상태 저장, 네트워크 모킹, 멀티탭, 파일 다운로드, 시각 회귀)은 `references/patterns.md` 참고.

## 5. 디버깅 도구

- **`PWDEBUG=1 npx playwright test`** — Inspector 창. 한 줄씩 step.
- **`npx playwright test --debug`** — 같은 효과.
- **`npx playwright show-trace trace.zip`** — 실패한 실행의 타임라인·네트워크·콘솔·DOM 스냅샷 재생.
- **`page.pause()`** — 코드 한 줄로 그 지점에서 멈추기.
- **`await page.locator(...).highlight()`** — 어느 요소가 잡혔는지 시각 확인.

## 6. 설치·트러블슈팅

### 처음 설치 (Windows)

```powershell
npm install -g playwright @playwright/test
playwright install msedge   # 시스템 Edge가 이미 있으면 "already installed" 메시지가 정상
```

전역이 싫으면 작업 디렉토리에서 `npm i -D` 로 로컬 설치.

### 환경에 Edge가 없을 때 fallback

Linux 컨테이너나 macOS 일부 환경엔 Edge가 없다. 이 경우 **사용자에게 한 번 알리고** `channel: 'msedge'` 를 빼서 Playwright 기본 chromium을 쓴다:

```javascript
const browser = await chromium.launch({ headless: true });   // 기본 chromium
```

또는 Edge를 설치: `npx playwright install msedge --with-deps` (Linux는 root 권한 필요).

### "already installed" 경고

`playwright install msedge` 실행 시 "msedge is already installed on the system" 메시지가 나오면 **정상**이다. Playwright는 별도 브라우저를 다운로드하지 않고 시스템 Edge를 쓰겠다는 뜻. `--force` 는 시스템 Edge를 *재설치*하라는 의미라서 보통 누르지 않는다.

### 한국어/유니코드 입력

`page.fill()`, `page.type()` 은 UTF-8 그대로 잘 동작한다. 다만 IME가 활성화된 헤디드 모드에서 `type()` 이 자모로 분리될 수 있으면 `fill()` 을 쓴다.

### CI에서 실행

CI 러너엔 보통 Edge가 없으니 기본 chromium을 쓰거나, GitHub Actions라면 `microsoft/playwright-github-action` 또는 공식 도커 이미지 `mcr.microsoft.com/playwright:v<버전>-jammy` 를 사용한다.

## 7. 출력 형식 — 사용자에게 결과를 보고할 때

테스트 실행 후 보고는 이 형태로:

```
✓/✗ <테스트 이름>  (<duration>ms)
  - 사용 브라우저: Edge (msedge)
  - URL: <최종 URL>
  - 캡처: <screenshot 경로 또는 "없음">
  - 실패 사유: <expect 메시지 또는 "n/a">
```

여러 케이스를 돌렸으면 Playwright의 HTML 리포트(`npx playwright show-report`)도 같이 안내한다.

## 8. 절대 하지 말 것

- **사용자 Chrome 프로필을 건드리지 말 것.** `launchPersistentContext` 로 `~/AppData/Local/Google/Chrome/User Data` 같은 경로를 지정하는 행위. 자동화는 Edge 또는 일회용 컨텍스트로.
- **`--no-sandbox` 를 무지성으로 붙이지 말 것.** 컨테이너에서 정말로 필요한 경우에만, 이유와 함께.
- **fixed `setTimeout`/`waitForTimeout` 으로 race condition 가리지 말 것.** 원인을 추적해 auto-retry assertion으로 표현.
- **헤디드에서만 통과하고 헤드리스에서 깨지는 테스트를 방치하지 말 것.** 보통 viewport, font, timing 의존성 문제. 헤드리스가 진실에 가깝다.
