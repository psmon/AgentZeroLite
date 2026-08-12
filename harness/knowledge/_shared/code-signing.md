# Code Signing — 실행파일 서명 획득 지식 (AgentZero Lite)

배포 산출물(`AgentZeroLite.exe`, Inno Setup `*-Setup.exe`)을 **공개 신뢰(Public Trust)**
코드 서명해 SmartScreen/백신 경고를 줄이기 위한 CA 선정·발급·CI 배선 전체 지식.

> **현재 상태: 전체 보류(ON HOLD).** CA는 **Certum(개인 실명, Open Source Cloud)** 으로
> 방향 확정, 상점 가입까지 완료. **신원검증(여권 제출 등)이 까다로워 진행 일시 중단.**
> Azure Trusted Signing 경로는 **철회·리소스 삭제 완료**(아래 경위).

---

## 0. 2026년 핵심 변화 (판단 기준이 바뀜)

- **EV의 "SmartScreen 즉시 무경고" 이점이 2026 MS 업데이트로 사라짐.** → 비싼 EV를
  살 이유가 크게 줄었다. OV/Open Source로 충분(경고는 다운로드 평판 축적으로 감소).
- **인증서 최대 유효기간 459일 상한**(2026-02-27~). 다년 구매 시 기간 중 무료 재발급 필요.
- 실무 결론: **저렴한 Open Source/OV + 타임스탬프 서명 + 시간에 따른 평판 축적**이 합리적.

---

## 1. CA 옵션 비교 (2026 기준)

| CA | 연비용(대략) | 클라우드 CI | 한국 발급 | 비고 |
|---|---|---|---|---|
| Azure Trusted Signing | ~$120 (월 $9.99) | ✅ 네이티브 | ❌ **리전 미지원** | 최저가였으나 한국서 신원검증 불가 → 철회 |
| **Certum Open Source (Cloud)** | **€49** | ⚠️ OTP·2h 창(로컬 서명 권장) | ✅ | **채택**. 개인 실명. 최저가 |
| Certum Standard OV (Cloud) | €209 | ⚠️ 동일 | ✅ | 회사(blumn) 명의 원할 때 |
| SSL.com OV + eSigner OV | ~$430 ($249+$180) | ✅ eSigner | ✅ | 클라우드 CI 깔끔, 중간가 |
| SSL.com EV + eSigner EV | ~$1,249 ($349+$900) | ✅ | ✅ | 2026엔 즉시무경고 사라져 가성비↓ |

**선정 결과:** AgentZero는 공개 GitHub 리포 → **Certum Open Source Code Signing (Cloud/SimplySign),
개인 실명, €49/년**. (다운로드 규모 소량 → 최저가 우선.)

---

## 2. Azure Trusted Signing — 시도·철회 경위 (재시도 금지 근거)

- 계정 생성까지 성공: RG/계정 `agent-trust`(koreacentral, Basic), endpoint
  `https://krc.codesigning.azure.net/`, `Artifact Signing Identity Verifier` 역할 부여.
- **차단 지점:** 신원검증(Identity Validation)이 **Microsoft Entra Verified ID** 방식인데,
  검증 카드(VerifiableCredential) **발급 경로가 한국 리전에 미제공** → "카드 없음 + 경고,
  추가 버튼 없음"으로 데드엔드. 포털도 "Artifact Signing is currently supported in
  **few countries/regions**" 경고. → **한국에서는 완료 불가.**
- **과금 주의(교훈):** Basic SKU는 **인증서 발급과 무관하게 "계정 존재"에 정액 월 $9.99**
  과금(일할). 신원검증 미완료·서명 0회여도 미터가 돈다.
- **정리 완료:** `az group delete -n agent-trust --yes` → RG/계정/역할 삭제, 과금 중단 확인
  (`az group exists` → false, `az trustedsigning list` → []). CSP(파트너) 청구라 사내 빌링
  담당에 공유 권장.
- **결론: 한국 사업자/개인은 Trusted Signing 경로 회피.**

---

## 3. Certum 경로 (채택, 신원검증에서 보류)

### 3.1 제품·가격 (연, EUR) — 반드시 **Cloud(SimplySign)** 선택

| 제품 | Cloud | 물리카드+리더 |
|---|---|---|
| **Open Source** | **€49** ← 채택 | €69 |
| Standard (OV) | €209 | €169 |
| EV | €379 | €359 |

가입/구매: **https://shop.certum.eu/code-signing.html**

### 3.2 발급 절차 (5단계)

1. **구매** — Open Source Code Signing → **"in the Cloud / SimplySign"** €49 결제. (card set 아님)
2. **신원검증(보류 지점)** — 개인 실명 확인. **여권**(해외발급) 제출 + 온라인/화상 또는 공증.
   Open Source라 **오픈소스 활동 증빙**(리포 `github.com/psmon/AgentZeroLite`, 주문 이메일 연결)도
   요구될 수 있음. 승인 1~5영업일. **← 여기서 절차가 까다로워 전체 보류.**
3. **SimplySign 앱** — 폰: SimplySign 모바일(OTP 토큰), PC: SimplySign Desktop(가상 스마트카드 브릿지).
4. **활성화/키 생성** — 이메일 활성화 링크 → 키 생성 방식 **"Certificate stored in the cloud"**
   (클라우드 HSM/가상카드) → 인증서 발급.
5. **서명** — SimplySign Desktop 로그인(이메일+모바일 OTP) → 서명 **2시간 창** 활성 →
   `signtool sign /tr http://time.certum.pl /td sha256 /fd sha256 /a <file>`

### 3.3 공식 링크

- 활성화·설치 매뉴얼(PDF): `https://files.certum.eu/documents/manual_en/CS-Code_Signing_in_the_Cloud_Certificate_activation.pdf`
- signtool 서명 매뉴얼(PDF): `https://www.files.certum.eu/documents/manual_en/Signing_with_the_use_of_jarsigner_tool_and_signtool.pdf`
- 상점: `https://shop.certum.eu/code-signing.html`

---

## 4. CI 배선 방침 (인증서 발급 후)

- Certum SimplySign은 **모바일 OTP + 2시간 창** → **GitHub Actions 무인 자동 서명 부적합.**
- **채택 방식: 빌드는 CI, 서명은 로컬.**
  1. `release.yml`은 지금처럼 미서명 산출물(Setup.exe, zip) 생성·릴리스 업로드.
  2. 로컬 PC(SimplySign Desktop 로그인 상태)에서 `signtool` 서명.
  3. 서명본으로 **릴리스 에셋 교체**(`gh release upload --clobber`).
- 후속 작성 예정: **"릴리스 에셋 다운로드 → 로컬 signtool 서명 → 재업로드" PowerShell 스크립트.**
- (완전 클라우드 무인 서명이 꼭 필요해지면 SSL.com eSigner OV가 대안 — 비용 ~$430/년.)

---

## 5. 재개 체크포인트

- [x] CA 선정: Certum Open Source (Cloud), 개인 실명
- [x] shop.certum.eu 가입
- [ ] 제품 구매(€49, Cloud/SimplySign 확인)
- [ ] **신원검증(여권 등) — 보류 중, 재개 시 첫 관문**
- [ ] SimplySign 모바일/데스크톱 설치·활성화
- [ ] 키 생성(cloud) + 인증서 발급
- [ ] 로컬 signtool 서명 테스트 (`AgentZeroLite-vX-Setup.exe`)
- [ ] 릴리스 에셋 교체 스크립트 작성/적용

## 6. 참조

- 릴리스 파이프라인: `.github/workflows/release.yml`, `installer/AgentZeroLite.iss`
- 릴리스 스킬: `.claude/skills/agent-zero-build`
- 타임스탬프(`/tr`)는 항상 포함 — 인증서 만료 후에도 서명 유효.
