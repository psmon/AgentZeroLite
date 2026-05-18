---
id: M0021
title: Remote Shell (SSH) 지원 — CMD/PW5/PW7 CLI 정의에 원격 모드 추가
operator: psmon
language: ko
status: done
priority: medium
created: 2026-05-18
related: [M0020]
started: 2026-05-18T10:00:00+09:00
finished: 2026-05-18T11:30:00+09:00
---

# 요청 (Brief)

터미널 설정을 위한 RemoteShell 기능을 도입하려고합니다.

현재 다음과같이 Command 뒤에 옵션을 주면 ssh가 가능합니다.
```
-NoExit -Command ssh psmac@192.168.0.50
```

이 설정을 remote 가 체크되면.. ssh를 수행하며
ip,id 를 입력해 저장을합니다.
인증 방식이 public key 인 경우는 pem파일을 load해 설정합니다.
pw 방식인 경우 입력해 저장합니다.

CMD,파워쉘5,7 에서 ssh remote를 지원합니다.
pw저장은 암호화되어 저장됩니다. 플레인 텍스트로 저장하지 않습니다.

## Acceptance

- [ ] `CliDefinition` 엔티티에 IsRemote/SshHost/SshUser/SshAuthMethod/SshKeyPath/EncryptedPassword 추가
- [ ] EF Core 마이그레이션 생성 + 기존 DB 자동 업그레이드
- [ ] DPAPI(CurrentUser) 기반 비밀번호 암호화 — 평문은 절대 디스크에 저장하지 않음
- [ ] `SshCommandBuilder` (ZeroCommon, 헤드리스 테스트 가능) — CMD/PowerShell5/PowerShell7 셸별로 ssh 명령 합성
- [ ] CLI Definition 편집 다이얼로그에 Remote 섹션 추가 (Host/User/AuthMode/PEM 또는 Password)
- [ ] 터미널 탭 실행 시 SSH 설정이 자동 주입됨 (Remote=true 인 정의 선택 시)
- [ ] Password 모드는 평문을 ssh 에 전달하지 않고 클립보드 + 토스트 안내로 처리
- [ ] `ZeroCommon` + `AgentZeroWpf` 빌드 0 오류
- [ ] 헤드리스 단위 테스트 (`SshCommandBuilder` + DPAPI 시암)
