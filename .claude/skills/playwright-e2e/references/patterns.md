# Playwright 고급 패턴

SKILL.md 본문에 다 넣기엔 길어서 분리한 자주 쓰는 패턴들. 사용자의 요청이 아래 항목 중 하나와 명확히 매칭되면 이 파일을 읽고 코드 작성에 반영한다.

## 목차

- [인증 상태 저장·재사용 (storageState)](#인증-상태-저장재사용-storagestate)
- [네트워크 가로채기·모킹](#네트워크-가로채기모킹)
- [파일 다운로드 검증](#파일-다운로드-검증)
- [파일 업로드](#파일-업로드)
- [멀티 탭·팝업·iframe](#멀티-탭팝업iframe)
- [시각 회귀 (visual snapshot)](#시각-회귀-visual-snapshot)
- [API + UI 혼합 — 백엔드로 셋업하고 UI로 검증](#api--ui-혼합--백엔드로-셋업하고-ui로-검증)
- [모바일 에뮬레이션](#모바일-에뮬레이션)
- [지오로케이션·권한·시간대](#지오로케이션권한시간대)
- [성능 측정](#성능-측정)
- [Page Object Model](#page-object-model)

## 인증 상태 저장·재사용 (storageState)

매 테스트마다 로그인하면 느리고 깨지기 쉽다. 한 번 로그인해 `storageState`(쿠키·localStorage)를 파일에 저장해 두고 다른 테스트가 재사용한다.

`tests/auth.setup.ts`:

```typescript
import { test as setup } from '@playwright/test';

const authFile = 'playwright/.auth/user.json';

setup('authenticate', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill(process.env.TEST_EMAIL!);
  await page.getByLabel('Password').fill(process.env.TEST_PW!);
  await page.getByRole('button', { name: '로그인' }).click();
  await page.waitForURL('/dashboard');
  await page.context().storageState({ path: authFile });
});
```

`playwright.config.ts`:

```typescript
projects: [
  { name: 'setup', testMatch: /.*\.setup\.ts/ },
  {
    name: 'edge-desktop',
    use: { channel: 'msedge', storageState: 'playwright/.auth/user.json' },
    dependencies: ['setup'],
  },
],
```

`playwright/.auth/` 는 `.gitignore` 에 추가.

## 네트워크 가로채기·모킹

API 응답을 가짜로 갈아치워 엣지 케이스(서버 500, 빈 배열, 느린 응답)를 UI 측면에서 검증.

```typescript
test('빈 목록일 때 placeholder 표시', async ({ page }) => {
  await page.route('**/api/items', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.goto('/items');
  await expect(page.getByText('아직 항목이 없습니다')).toBeVisible();
});

test('서버 에러 시 재시도 버튼', async ({ page }) => {
  await page.route('**/api/items', route => route.fulfill({ status: 500 }));
  await page.goto('/items');
  await expect(page.getByRole('button', { name: '재시도' })).toBeVisible();
});
```

특정 요청을 차단(분석 스크립트 등):

```typescript
await page.route(/google-analytics|hotjar|sentry/, route => route.abort());
```

## 파일 다운로드 검증

```typescript
test('CSV 내보내기', async ({ page }) => {
  await page.goto('/reports');
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('button', { name: 'CSV 내보내기' }).click(),
  ]);
  expect(download.suggestedFilename()).toMatch(/report-\d{4}-\d{2}-\d{2}\.csv/);
  await download.saveAs(`./artifacts/${download.suggestedFilename()}`);
});
```

## 파일 업로드

```typescript
await page.getByLabel('이미지 첨부').setInputFiles('./fixtures/avatar.png');
// 드래그앤드롭 input이면:
await page.getByLabel('이미지 첨부').setInputFiles({
  name: 'avatar.png',
  mimeType: 'image/png',
  buffer: Buffer.from(/* ... */),
});
```

## 멀티 탭·팝업·iframe

새 탭 열림:

```typescript
const [newTab] = await Promise.all([
  context.waitForEvent('page'),
  page.getByRole('link', { name: '약관' }).click(),
]);
await newTab.waitForLoadState();
await expect(newTab).toHaveURL(/terms/);
```

iframe:

```typescript
const frame = page.frameLocator('iframe[name="payment"]');
await frame.getByLabel('카드 번호').fill('4111 1111 1111 1111');
```

## 시각 회귀 (visual snapshot)

```typescript
await expect(page).toHaveScreenshot('home.png', { maxDiffPixelRatio: 0.01 });
```

첫 실행 시 baseline이 생성된다(`__snapshots__/`). 의도적 디자인 변경 후엔 `npx playwright test --update-snapshots`. CI에서는 baseline OS·browser가 로컬과 다르면 작은 anti-aliasing 차이로 깨질 수 있으니 같은 환경(보통 도커 이미지)에서 생성한다.

## API + UI 혼합 — 백엔드로 셋업하고 UI로 검증

테스트 데이터를 UI로 만들지 말 것. 매번 느리고 다른 기능 변경에 흔들린다. API로 만들고 UI는 *검증*만.

```typescript
test('만들어진 주문이 목록에 보인다', async ({ page, request }) => {
  const res = await request.post('/api/orders', {
    data: { itemId: 42, qty: 2 },
    headers: { Authorization: `Bearer ${process.env.API_TOKEN}` },
  });
  const { id } = await res.json();

  await page.goto('/orders');
  await expect(page.getByTestId(`order-${id}`)).toBeVisible();
});
```

## 모바일 에뮬레이션

```typescript
import { devices } from '@playwright/test';

// config.ts projects 안에:
{ name: 'mobile', use: { ...devices['Pixel 7'], channel: 'msedge' } },
```

`devices` 는 viewport·UA·터치 이벤트·devicePixelRatio 를 한 번에 설정한다. 실제 Edge for Android와 완전히 같진 않지만 99%의 반응형 회귀는 잡힌다.

## 지오로케이션·권한·시간대

```typescript
const context = await browser.newContext({
  geolocation: { latitude: 37.5665, longitude: 126.9780 },   // 서울
  permissions: ['geolocation', 'clipboard-read'],
  locale: 'ko-KR',
  timezoneId: 'Asia/Seoul',
});
```

## 성능 측정

```typescript
const metrics = await page.evaluate(() => JSON.stringify(performance.getEntriesByType('navigation')));
console.log(JSON.parse(metrics));
// 또는 CDP 직접:
const client = await page.context().newCDPSession(page);
await client.send('Performance.enable');
const perf = await client.send('Performance.getMetrics');
```

본격적인 web-vitals 측정은 `@playwright/test` + `web-vitals` 라이브러리 조합이 깔끔하다.

## Page Object Model

테스트가 10개 이상 되면 셀렉터를 한 곳에 모은다.

```typescript
// pages/LoginPage.ts
export class LoginPage {
  constructor(private page: import('@playwright/test').Page) {}
  goto = () => this.page.goto('/login');
  fillEmail = (v: string) => this.page.getByLabel('Email').fill(v);
  fillPassword = (v: string) => this.page.getByLabel('Password').fill(v);
  submit = () => this.page.getByRole('button', { name: '로그인' }).click();
}

// tests/login.spec.ts
import { LoginPage } from '../pages/LoginPage';
test('login', async ({ page }) => {
  const lp = new LoginPage(page);
  await lp.goto();
  await lp.fillEmail('a@b.com');
  await lp.fillPassword('x');
  await lp.submit();
});
```

과하게 추상화하지 말 것 — 셀렉터·작은 액션 묶음 정도. assertion은 spec에 그대로 두는 게 읽기 좋다.
