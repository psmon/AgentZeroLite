# Code Signing — Azure Trusted Signing (AgentZero Lite)

배포 산출물(`AgentZeroLite.exe`, Inno Setup `*-Setup.exe`)을 **공개 신뢰(Public Trust)**
코드 서명해 SmartScreen/백신 경고를 없애기 위한 기술 절차. CA = **Azure Trusted Signing**
(구 Azure Code Signing / "Artifact Signing").

## 계정 사실 (생성 완료)

| 항목 | 값 |
|---|---|
| Subscription | `Microsoft Azure` (`e7d32b84-cef8-4b55-9033-b9bb88095354`) |
| Tenant | `b7936018-4601-4632-9e4a-4ff468beac72` |
| Resource Group | `agent-trust` |
| Account | `agent-trust` (Microsoft.CodeSigning/codeSigningAccounts) |
| Region | `koreacentral` |
| SKU | `Basic` (~$9.99/월, 월 5,000 서명 = 파일당 1회) |
| **Account URI (Endpoint)** | `https://krc.codesigning.azure.net/` ← CI 서명 config에 필요 |

## RBAC 역할 (2종)

| 역할 | 용도 | 부여 대상 |
|---|---|---|
| **Artifact Signing Identity Verifier** | 신원 검증 제출/관리 | 사람(포털 검증자) — smpark@blumn.io 에 계정 범위 부여 완료 |
| **Artifact Signing Certificate Profile Signer** | 실제 서명(CI) | CI 서비스 주체 — 인증서 프로필 생성 후 부여 |

## 진행 상태 (체크포인트)

- [x] `Microsoft.CodeSigning` 공급자 등록 (`Registered`)
- [x] `trustedsigning` CLI 확장 설치 (`1.0.0b2`)
- [x] RG + 계정 `agent-trust` 생성 (koreacentral, Basic)
- [x] `Artifact Signing Identity Verifier` → smpark@blumn.io (계정 범위)
- [~] **Identity Validation 제출됨 → 상태 InProgress** (Microsoft 심사 1~5영업일, 포털에서 제출)
- [ ] Certificate Profile 생성 (검증 승인 후, CLI)
- [ ] CI 서비스 주체 + `Certificate Profile Signer` 역할
- [ ] `release.yml` + `AgentZeroLite.iss` 서명 배선

## 사용한 CLI (재현용)

```bash
az provider register --namespace Microsoft.CodeSigning         # 공급자 등록
az extension add --name trustedsigning                         # CLI 확장
az group create -n agent-trust -l koreacentral                 # RG
az trustedsigning create -n agent-trust -g agent-trust \
    -l koreacentral --sku Basic                                # 계정
# 신원검증 역할 (사람)
az role assignment create \
  --assignee <userObjectId> \
  --role "Artifact Signing Identity Verifier" \
  --scope /subscriptions/<sub>/resourceGroups/agent-trust/providers/Microsoft.CodeSigning/codeSigningAccounts/agent-trust
```

> Identity Validation 제출 자체는 **포털 전용**(CLI 없음): 계정 → Identity validations →
> New → Organization(blumn) 또는 Individual → 제출.

## 승인 후 남은 절차 (기술)

1. **인증서 프로필 생성** (검증 ID 필요):
   ```bash
   az trustedsigning certificate-profile create \
     -g agent-trust --account-name agent-trust \
     -n <profileName> --profile-type PublicTrust \
     --identity-validation-id <approvedId>
   ```
2. **CI 서비스 주체 + 서명 역할**:
   ```bash
   az ad sp create-for-rbac -n azcs-agentzero-signer   # OIDC 연동 권장(secret 없이 federated credential)
   az role assignment create --assignee <spAppId> \
     --role "Artifact Signing Certificate Profile Signer" \
     --scope <account resourceId>
   ```
3. **release.yml 서명 스텝** — publish 후 exe 서명 → iscc → Setup.exe 서명.
   `azure/trusted-signing-action` (또는 `sign` dotnet 글로벌 툴 + `Azure.CodeSigning.Dlib`).
   필요 입력: endpoint `https://krc.codesigning.azure.net/`, account `agent-trust`,
   certificate-profile `<profileName>`, 대상 파일(exe, Setup.exe).
4. **AgentZeroLite.iss** — `[Setup]` 에 `SignTool=<tool> $f` + `SignedUninstaller=yes`,
   iscc 호출 시 `/S<tool>=...` 로 서명 명령 전달. (또는 iscc 후 Setup.exe만 signtool 서명)
5. secret 없을 때도 빌드는 계속되게(조건부) — 서명 실패로 릴리스가 막히지 않도록.

## 주의/제약

- **지원 국가/지역**: Trusted Signing 신원 검증은 일부 국가만 지원. blumn(한국)이
  목록에 있어야 Public Trust 발급 가능. 미지원 시 대안 CA:
  - **Certum**(개인/오픈소스 최저가, SimplySign 클라우드)
  - **SSL.com**(OV/EV, eSigner 클라우드, CI 친화)
- SmartScreen 평판: Trusted Signing/OV는 서명 후에도 초기 평판 축적 필요할 수 있음(EV는 즉시).
- 타임스탬프 필수(`/tr`) — 인증서 만료 후에도 서명 유효.
- 못 쓰게 되면 과금 중단: `az group delete -n agent-trust --yes`.

## 참조

- 릴리스 파이프라인: `.github/workflows/release.yml`, `installer/AgentZeroLite.iss`
- 릴리스 스킬: `.claude/skills/agent-zero-build`
