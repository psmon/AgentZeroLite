---
name: pencil-design
description: |
  Pencil MCP를 이용해 아키텍처 다이어그램, 시스템 설계도, 플로우차트, ERD 등 기술 모델링 스케치를 .pen 파일로 생성하는 스킬.
  설계 의도를 시각적으로 표현하는 데 집중하며, 코드가 아닌 다이어그램/설계도 산출물을 만든다.

  다음 상황에서 반드시 이 스킬을 사용할 것:
  - "펜슬을 이용해 OOO 그려줘", "펜슬로 OOO 설계도 그려"
  - "펜슬 이용해 다이어그램 그려", "펜슬로 아키텍처 그려줘"
  - ".pen 파일로 OOO 만들어줘", "디자인 파일로 OOO 스케치"
  - "시스템 구조도 그려줘", "플로우차트 그려줘" (펜슬/pen 언급 시)
  - "ERD 그려줘", "상태 다이어그램 그려줘" (펜슬/pen 언급 시)
  - design/ 디렉토리에 .pen 파일 생성이 필요할 때
  - "WPF 애니메이션 조사해서 펜슬로 그려줘", "WPF 컨트롤 디자인 아이디어"
  - "XAML Storyboard 시각화", "WPF Blend 애니메이션 서칭해서 디자인"
  - WPF/XAML 컨트롤의 애니메이션 상태 전환을 시각적으로 표현할 때
  - "WPF 템플릿 마이그레이션", "XAML을 WPF앱에 마이그레이션 해줘"
  - "Blend에서 편집할 수 있게 변환해줘", "design-wpf-app에 추가해줘"
  - "WPF 애니메이션 Blend 호환으로 변환", "XAML UserControl로 변환"
  - design/xaml/sample/ 의 XAML을 design-wpf-app/migrated/ 로 전환 요청 시
  - "이미지 생성해줘", "그림 만들어줘", "일러스트 생성", "이미지 그려줘"
  - "제미나이로 이미지 만들어", "제미나이 이용해 이미지 생성", "Gemini 이미지"
  - "제트이미지를 이용해 이미지생성", "z-image로 그려줘", "ComfyUI로 이미지", "로컬로 이미지 생성"
  - "OOO 내용으로 이미지 생성해줘" 패턴 모두
  - "이미지 편집해줘", "이 이미지를 수정해줘"
  - 펜슬 카드 삽화/배경/썸네일/HTML 페이지용 이미지가 필요할 때
allowed-tools: mcp__pencil__get_guidelines, mcp__pencil__open_document, mcp__pencil__get_editor_state, mcp__pencil__batch_design, mcp__pencil__get_screenshot, mcp__pencil__find_empty_space_on_canvas, mcp__pencil__snapshot_layout, mcp__pencil__batch_get, mcp__pencil__get_style_guide_tags, mcp__pencil__get_style_guide, Read, Write, Glob, Grep, WebSearch, WebFetch, Agent, Bash
---

# Pencil Design Skill — 기술 다이어그램 & 설계도 생성

Pencil MCP 도구를 사용하여 **아키텍처 다이어그램, 시스템 설계도, 플로우차트, 상태 다이어그램** 등 기술 모델링 스케치를 .pen 파일로 생성한다.
또한 **WPF/XAML 애니메이션 컨트롤 조사 → 디자인 아이디어 시각화 → XAML 샘플 아카이빙** 워크플로우를 지원한다.

---

## 1. 기본 설정

| 항목 | 값 |
|------|-----|
| 저장 위치 | `D:\MYNOTE\design\{파일명}.pen` |
| 기본 폰트 | Inter (없으면 시스템 기본) |
| 캔버스 배경 | `#F5F6FA` (밝은 회색) |
| 최대 캔버스 너비 | 1440px |

---

## 2. 워크플로우

```
Step 1. 주제 분석
  → 사용자 요청에서 다이어그램 유형 판별
  → 필요 데이터 수집 (파일 읽기, 구조 파악 등)

Step 2. 다이어그램 설계
  → 유형에 맞는 레이아웃 패턴 선택
  → 색상 팔레트 결정
  → 섹션별 구조 계획 (max 25 ops/call 제약 고려)

Step 3. Pencil MCP 실행
  → get_guidelines("web-app") 호출 (레이아웃 규칙 참조)
  → open_document("new") 또는 기존 파일 열기
  → get_editor_state(include_schema: true) — 스키마 파악
     ⚠️ 펜슬은 구동 중인데 응답이 없거나 창이 안 보이면 → §12 창 복원 먼저 수행
  → batch_design() — 섹션별로 나눠서 호출
  → get_screenshot() — 시각적 검증

Step 4. 검증 & 마무리
  → 스크린샷으로 레이아웃/텍스트 가독성 확인
  → placeholder 플래그 제거 확인
  → 파일 경로 사용자에게 안내
```

---

## 3. 다이어그램 유형별 레이아웃 패턴

### 3.1 아키텍처 다이어그램

계층 구조, 컴포넌트 관계, 시스템 전체 조감도를 표현한다.

```
레이아웃:
  메인 프레임 (vertical, padding 40, gap 32)
  ├── 타이틀 바 (center aligned)
  ├── 메인 행 (horizontal, gap 24)
  │   ├── 좌측 컬럼 (컴포넌트/계층)
  │   ├── 중앙 컬럼 (핵심 흐름)
  │   └── 우측 컬럼 (보조 정보)
  └── 하단 행 (horizontal, gap 24)
      ├── 상세 섹션 A
      └── 상세 섹션 B

색상 전략:
  각 컴포넌트/계층에 고유 색상 부여
  배경 + 테두리 조합 (fill + stroke)
  텍스트는 배경과 같은 계열의 진한 색
```

### 3.2 플로우차트 / 상태 다이어그램

순서, 분기, 루프를 시각화한다.

```
레이아웃:
  세로 흐름 (vertical, gap 12)
  각 상태 = 가로 행 (horizontal: 아이콘 + 상태명 + 설명)
  분기점 = 별도 배경색 노트 박스
  루프 = ↺ 기호 + 설명 텍스트

상태 표현:
  원형 dot (ellipse) + 배경색 원 (frame, cornerRadius: 14)
  상태별 고유 색상으로 시각적 구분
```

### 3.3 ERD / 데이터 모델

엔티티와 관계를 표현한다.

```
레이아웃:
  엔티티 = 카드형 프레임 (vertical: 제목 + 필드 목록)
  관계 = 엔티티 사이 텍스트 라벨
  그룹핑 = 배경색 영역으로 도메인 구분
```

### 3.4 그리드 / 매트릭스

비교표, 기능 매트릭스를 표현한다.

```
레이아웃:
  테이블 구조 (frame → row frame → cell frame → content)
  헤더 행 = 진한 배경 + 흰색 텍스트
  데이터 행 = 교대 배경색 (줄무늬)
```

---

## 4. 색상 팔레트 가이드

기술 다이어그램에 적합한 색상 조합. 각 섹션은 **배경(fill) + 테두리(stroke) + 텍스트(fill)** 3종 세트로 사용한다.

| 이름 | 배경 | 테두리 | 텍스트 (진) | 텍스트 (중) | 용도 |
|------|------|--------|------------|------------|------|
| Gold | #FEF3C7 | #F59E0B | #78350F | #92400E | 지식, 설정 |
| Cyan | #CFFAFE | #06B6D4 | #164E63 | #155E75 | 에이전트, 처리 |
| Rose | #FFE4E6 | #F43F5E | #881337 | #9F1239 | 엔진, 경고 |
| Green | #D1FAE5 / #F0FDF4 | #86EFAC | #166534 | #15803D | 발행, 성공 |
| Blue | #DBEAFE | #3B82F6 | #1E40AF | #1D4ED8 | 기본, 유틸 |
| Purple | #EDE9FE | #8B5CF6 | #5B21B6 | #7C3AED | 핵심, 스킬 |
| Orange | #FFF7ED | #FDBA74 | #9A3412 | #C2410C | 평가, 메트릭 |
| Dark | #1E1B4B | #312E81 | #C4B5FD | #A5B4FC | 강조 섹션 |
| Neutral | #FFFFFF | #E5E7EB | #1B1F3B | #6B7280 | 카드, 컨테이너 |

---

## 5. batch_design 핵심 규칙

Pencil MCP의 batch_design은 한 번에 **최대 25개 오퍼레이션**만 실행 가능하다. 복잡한 다이어그램은 논리적 섹션별로 나눠서 호출한다.

### 실행 순서

```
1. 메인 컨테이너 생성 (placeholder: true)
2. 타이틀 + 서브타이틀
3. 각 섹션별 batch_design 호출 (좌측, 중앙, 우측 등)
4. 하단 섹션
5. placeholder: false 설정
6. get_screenshot()로 확인
```

### 필수 체크리스트

- [ ] 모든 텍스트에 `fill` 속성 설정 (없으면 투명 = 보이지 않음)
- [ ] 프레임에 `width`/`height` 명시 (`fill_container` 또는 숫자)
- [ ] flexbox 자식에는 `x`/`y` 미사용 (무시됨)
- [ ] `placeholder: true`로 시작, 완료 시 `false`
- [ ] 텍스트 줄바꿈 시 `textGrowth: "fixed-width"` + `width` 필수
- [ ] 이미지는 `type: "image"` 아님 — frame 생성 후 `G()` 오퍼레이션 사용
- [ ] 바인딩 이름은 batch_design 호출 간 재사용 불가

### 자주 쓰는 패턴

```javascript
// 카드형 섹션
card=I(parent, {type: "frame", layout: "vertical", width: "fill_container",
  height: "fit_content", fill: "#FFFFFF", cornerRadius: 12, padding: 16,
  gap: 8, stroke: {fill: "#E5E7EB", thickness: 1}})

// 색상 뱃지 + 텍스트 행
row=I(parent, {type: "frame", layout: "horizontal", width: "fill_container",
  height: "fit_content", gap: 8, alignItems: "center"})
badge=I(row, {type: "frame", width: 24, height: 24, fill: "#3B82F6",
  cornerRadius: 6, layout: "vertical", justifyContent: "center", alignItems: "center"})
label=I(badge, {type: "text", content: "A", fontSize: 11,
  fontWeight: "bold", fill: "#FFFFFF", fontFamily: "Inter"})

// 상태 dot (플로우차트용)
icon=I(row, {type: "frame", width: 28, height: 28, fill: "#D1FAE5",
  cornerRadius: 14, layout: "none"})
dot=I(icon, {type: "ellipse", width: 10, height: 10, fill: "#10B981", x: 9, y: 9})

// 경고/노트 박스
note=I(parent, {type: "frame", layout: "horizontal", width: "fill_container",
  height: "fit_content", fill: "#FEF2F2", cornerRadius: 8,
  padding: [8, 12, 8, 12], gap: 6, alignItems: "center"})

// 그림자 있는 카드
shadow=I(parent, {type: "frame", ..., effect: {type: "shadow",
  shadowType: "outer", blur: 12, color: "#0000000F", offset: {x: 0, y: 4}}})
```

---

## 6. 안티패턴

| 안티패턴 | 결과 | 올바른 방법 |
|----------|------|-------------|
| 텍스트에 fill 미설정 | 투명 텍스트 (보이지 않음) | 반드시 fill: "#색상" 지정 |
| 한 번에 25+ 오퍼레이션 | 실행 실패 가능 | 섹션별로 나눠서 호출 |
| placeholder 미제거 | 미완성 상태로 남음 | 완료 시 반드시 false |
| flexbox 자식에 x/y 설정 | 무시되어 의도와 다른 배치 | gap/padding/alignItems 사용 |
| 바인딩 이름 재사용 | 참조 오류 | 호출마다 고유한 이름 사용 |
| 이미지를 type: "image"로 생성 | 존재하지 않는 타입 | frame 생성 후 G() 사용 |
| 스크린샷 미확인 | 레이아웃 오류 미발견 | 완료 후 반드시 get_screenshot() |

---

## 7. WPF/XAML 디자인 아이디어 워크플로우

WPF Blend for Visual Studio의 애니메이션 컨트롤/화면을 조사하고, 펜슬에 디자인 아이디어로 시각화하는 워크플로우이다.
이 기능의 핵심 목적은 **WPF XAML에 정의된 애니메이션 패턴을 학습하고, 펜슬의 디자인 표현력으로 상태 전환을 시각화**하는 것이다.

### 7.1 워크플로우 단계

```
Step 1. WPF 애니메이션 리서치
  → WebSearch/Agent로 모던 WPF Storyboard 애니메이션 예제 조사
  → 핵심 기술: EventTrigger, DoubleAnimation, ColorAnimation, TranslateTransform 등
  → 각 컨트롤별 XAML 코드 스니펫 수집

Step 2. 펜슬 디자인 아이디어 생성
  → design/wpf-animation.pen (또는 사용자 지정 .pen 파일)에 시각화
  → 각 애니메이션을 "상태 전환 카드"로 표현
    - 제목 + 번호 (JetBrains Mono, cyan 악센트)
    - 애니메이션 설명 (Inter, 회색)
    - 시각적 미리보기 (상태 Before → After 화살표 연결)
    - 핵심 XAML 코드 스니펫 (코드 블록 스타일)
  → 다크 테마 권장: #0A0F1C 배경, #1E293B 카드, #22D3EE 악센트

Step 3. XAML 메타 아카이빙
  → design/xaml/research-history.md — 출처/획득경로 히스토리
  → design/xaml/sample/*.xaml — 완전한 Window XAML 샘플 파일
  → 기존 정보 보강 및 최신 정보 추가 시 같은 경로에 업데이트
```

### 7.2 디자인 아이디어 카드 패턴

각 WPF 애니메이션 컨트롤을 하나의 카드로 표현한다:

```javascript
// 카드 구조 (dark theme)
card=I(row, {type: "frame", layout: "vertical", width: "fill_container",
  height: "fit_content(480)", fill: "#1E293B", cornerRadius: 12,
  padding: [24, 24, 24, 24], gap: 20})

// 번호 + 제목 (cyan 악센트)
title=I(card, {type: "text", content: "01  GLASS EFFECT BUTTON",
  fontFamily: "JetBrains Mono", fontSize: 14, fontWeight: "700",
  fill: "#22D3EE", letterSpacing: 2})

// 애니메이션 설명
desc=I(card, {type: "text", content: "MouseEnter → Glass overlay fades in...",
  fontFamily: "Inter", fontSize: 13, fill: "#94A3B8",
  textGrowth: "fixed-width", width: "fill_container", lineHeight: 1.5})

// 시각적 미리보기 (상태 전환)
vis=I(card, {type: "frame", layout: "none", width: "fill_container",
  height: 180, fill: "#0F172A", cornerRadius: 8})
// → 내부에 Before 상태 + "→" 화살표 + After 상태 배치

// XAML 코드 스니펫 블록
codeBlock=I(card, {type: "frame", layout: "vertical",
  width: "fill_container", height: "fit_content(80)",
  fill: "#0A0F1C", cornerRadius: 6, padding: [12, 16, 12, 16], gap: 4})
codeLine=I(codeBlock, {type: "text", content: "<Storyboard ...>",
  fontFamily: "JetBrains Mono", fontSize: 10, fill: "#22D3EE"})
```

### 7.3 상태 전환 시각화 기법

애니메이션은 정지 이미지로 표현해야 하므로 다음 패턴을 사용한다:

| 애니메이션 유형 | 시각화 방법 |
|--------------|-----------|
| Hover 전환 | Normal 상태 → 화살표 → Hover 상태 나란히 배치 |
| 색상 변화 | 3단계 그라데이션 (Normal → Transition → Active) |
| 크기 변화 | 작은 상태 → 큰 상태 오버레이 또는 나란히 |
| 회전 | 0° 상태 + 회전 호 표시 (sweepAngle) |
| 슬라이드 | 위치 이동 전후를 투명도 차이로 표현 |
| 무한 반복 | RepeatBehavior 아이콘(∞) + 단일 프레임 캡처 |

### 7.4 design/xaml 디렉토리 구조

```
design/xaml/
├── research-history.md    ← 조사 히스토리 (출처 URL, 핵심 기술, 날짜)
└── sample/                ← 완전한 XAML 샘플 파일
    ├── 01-glass-effect-button.xaml
    ├── 02-animated-sidebar-menu.xaml
    └── ...
```

- `research-history.md`: 새로운 조사 시 기존 내용에 **추가**한다 (덮어쓰기 X)
- `sample/`: 번호-이름.xaml 패턴으로 저장, WPF Window 단위로 독립 실행 가능해야 함
- 기존 정보가 있으면 읽고 보강하며, 중복 항목은 최신 정보로 업데이트

### 7.5 WPF 애니메이션 핵심 키워드

조사 시 다음 키워드를 중심으로 검색한다:

- **Storyboard**: EventTrigger, BeginStoryboard, RepeatBehavior
- **Transform**: ScaleTransform, RotateTransform, TranslateTransform
- **Animation**: DoubleAnimation, ColorAnimation, ThicknessAnimation
- **Easing**: CubicEase, QuadraticEase, AccelerationRatio/DecelerationRatio
- **Control**: ControlTemplate, DataTemplate, Style.Triggers
- **Modern**: Material Design, Fluent Design, WPF UI (lepoco/wpfui)

---

## 8. 멀티 에이전트 분산 디자인 (최대 3개 동시)

Pencil MCP는 여러 Agent가 동일 .pen 파일에 **동시에** batch_design을 호출할 수 있다.
대량의 카드/섹션을 만들 때 최대 3개 에이전트를 병렬로 투입하면 작업 속도가 3배 향상된다.

### 8.1 분산 처리 원칙

```
핵심 규칙: 각 에이전트는 서로 다른 프레임(노드)에만 작업한다 — 절대 겹치지 않을 것

준비 단계 (메인 스레드):
  1. 전체 레이아웃 계획 수립
  2. 카테고리별 컨테이너 프레임을 미리 생성 (placeholder: true)
     - 각 컨테이너에 제목/설명은 메인에서 삽입
     - 카드가 들어갈 row 프레임의 ID를 확보
  3. 에이전트별 담당 프레임 ID를 명확히 할당

분산 단계 (최대 3개 에이전트 동시):
  Agent 1 → 프레임 A (예: cat2row "4R0Kr")의 카드 3개
  Agent 2 → 프레임 B (예: cat3row "fOqok")의 카드 3개
  Agent 3 → 프레임 C (예: cat4row "o5CZl")의 카드 3개

완료 단계 (각 에이전트 독립):
  → 자기 담당 프레임의 placeholder: false 설정
  → get_screenshot()으로 자기 영역 검증
```

### 8.2 에이전트 프롬프트 템플릿

각 에이전트에게 다음 정보를 반드시 전달한다:

```
1. 파일 경로: D:\MYNOTE\design\{파일명}.pen
2. 대상 row 프레임 ID: "{row-id}" — 이 프레임에만 카드를 삽입할 것
3. 부모 컨테이너 ID: "{container-id}" — 완료 시 placeholder: false 설정할 대상
4. 카드 디자인 규격 (fill, cornerRadius, padding, gap, 폰트 등)
5. 각 카드별 구체적 내용 (제목, 설명, 시각적 요소, 코드 스니펫)
6. 완료 후 get_screenshot으로 검증 지시
```

### 8.3 레이아웃 충돌 방지

| 규칙 | 설명 |
|------|------|
| 프레임 ID 분리 | 각 에이전트는 할당된 row 프레임 ID 내부에서만 I() 호출 |
| 위치 고정 | 컨테이너 프레임의 x/y는 메인에서 미리 배치 — 에이전트가 변경하지 않음 |
| placeholder 관리 | 각 에이전트가 자기 부모 컨테이너만 placeholder: false 처리 |
| 바인딩 이름 | 에이전트 간 바인딩 이름 충돌 없음 (각 batch_design 호출은 독립적) |
| 스크린샷 | 각 에이전트는 자기 컨테이너 ID로만 get_screenshot() 호출 |

### 8.4 적용 시나리오

```
시나리오 A: 카테고리별 분산 (가장 일반적)
  → 4개 카테고리 중 3개를 동시에, 1개는 메인에서 처리
  → 메인: Cat1 직접 제작 + Cat2/3/4 컨테이너 생성
  → Agent 1/2/3: Cat2, Cat3, Cat4 각각 담당

시나리오 B: 대형 다이어그램 섹션별 분산
  → 좌측/중앙/우측 영역을 각각 다른 에이전트가 담당
  → 메인: 전체 프레임 + 3개 컬럼 프레임 생성
  → Agent 1/2/3: 각 컬럼 내부 콘텐츠 제작

시나리오 C: 디자인 + XAML 샘플 병렬
  → 펜슬 디자인과 XAML 파일 생성을 동시에 진행
  → Agent 1: 펜슬 카드 디자인 (batch_design)
  → Agent 2: XAML 샘플 파일 06~11 작성 (Write)
  → Agent 3: XAML 샘플 파일 12~17 작성 (Write)
```

### 8.5 안티패턴

| 안티패턴 | 결과 | 올바른 방법 |
|----------|------|-------------|
| 같은 프레임 ID에 2개 에이전트 동시 작업 | 노드 충돌, 예측 불가 결과 | 프레임 ID를 엄격히 분리 |
| 컨테이너 미생성 상태에서 에이전트 투입 | 대상 프레임 없어 실패 | 메인에서 반드시 컨테이너 먼저 생성 |
| 4개 이상 에이전트 동시 투입 | 성능 저하, 타임아웃 위험 | 최대 3개까지만 동시 실행 |
| 에이전트에게 프레임 ID 미전달 | 잘못된 위치에 삽입 | 프롬프트에 정확한 ID 명시 |
| 메인이 에이전트 작업 영역을 수정 | 충돌 발생 | 에이전트 완료까지 해당 영역 접근 금지 |

---

## 9. 디자인 평가 트리거

디자인 작업 완료 시 `harness/knowledge/design-craft.md` 기준으로 평가를 발동한다.

### 9.1 Case P 발동 조건 (WPF → Pencil)

```
다음 조건 모두 충족 시 자동 발동:
  1. WPF/XAML 관련 리서치를 수행했음
  2. .pen 파일에 신규 컴포넌트/카드를 추가했음
  3. get_screenshot()으로 시각적 검증을 완료했음

평가 축: P1(신규성 35) + P2(시각화 35) + P3(메타 완결성 30)
```

### 9.2 Case W 발동 조건 (Pencil → HTML)

```
다음 조건 모두 충족 시 자동 발동:
  1. .pen 파일을 batch_get/get_screenshot 등으로 참조했음
  2. HTML/CSS/JS 파일을 생성했음
  3. 생성된 HTML이 .pen 디자인 요소를 적용한 것임

평가 축: W1(커버리지 35) + W2(애니메이션 충실도 35) + W3(독창적 확장 30)
```

### 9.3 Pipeline P→W 보너스

```
Case P와 Case W가 같은 세션에서 연속 발동 시:
  양쪽 모두 ≥ 60점 → 각 XP에 1.3배 보너스
```

---

## 10. 디자인 익스포트 & HTML 생성 워크플로우

### 10.1 .pen → HTML 변환 워크플로우

Pencil 디자인 파일의 애니메이션 컴포넌트를 실제 HTML/CSS/JS로 구현하는 워크플로우.

```
Step 1. 디자인 요소 파악
  → get_editor_state() / snapshot_layout()으로 .pen 파일 구조 파악
  → get_screenshot()으로 각 카테고리의 시각적 요소 확인
  → 애니메이션 카드의 Before→After 패턴과 XAML 스니펫 수집

Step 2. 애니메이션 매핑 설계
  → .pen의 각 컴포넌트를 HTML/CSS 기법으로 대응 설계
  → 매핑 테이블 작성 (pen 이펙트 → CSS/JS 구현 위치)

  대응 규칙:
    WPF DoubleAnimation → CSS transition / @keyframes
    WPF ColorAnimation → CSS color transition
    WPF ScaleTransform → CSS transform: scale()
    WPF RotateTransform → CSS transform: rotate()
    WPF TranslateTransform → CSS transform: translate()
    WPF ElasticEase → CSS cubic-bezier(.68,-.55,.265,1.55)
    WPF RepeatBehavior=Forever → CSS animation: infinite

Step 3. HTML 생성
  → design/xaml/output/sample{N}/index.html에 저장
  → 다크 테마 권장 (var(--bg), var(--cyan) 등 CSS 변수)
  → 반응형 + 인터랙티브 + 스크롤 트리거 애니메이션 포함

Step 4. Playwright 캡처 (선택 — 사용자 요청 시에만)
  ⚠️ 자동 실행하지 않음. "스크린샷 캡처해줘" 등 명시적 요청 시에만 수행.
  → python -m http.server로 로컬 서버 시작
  → browser_navigate → 섹션별 scrollIntoView → waitForTimeout → take_screenshot
  → tmp/playwright/sample{N}/에 섹션별 PNG 저장

Step 5. 위키 게시
  → 캡처 이미지 + HTML 파일을 wiki에 attach
  → 각 섹션 이미지에 적용된 애니메이션 설명 추가
```

### 10.2 output 디렉토리 구조

```
design/xaml/output/
├── sample00/             ← 디자인 테스트 샘플 페이지
│   ├── index.html
│   ├── style.css
│   └── app.js
└── sample{N}/            ← 추가 테스트 샘플 페이지
```

### 10.3 Playwright 섹션별 캡처 패턴 (사용자 요청 시에만)

```javascript
// 1. 로컬 서버 시작
// python -m http.server 8765 (design/xaml/output/sample{N}/ 에서)

// 2. 브라우저 설정
browser_resize(1400, 900)
browser_navigate("http://localhost:8765/")

// 3. 섹션별 반복
for each section_id in [hero, about, travels, gallery, stats, journey, contact]:
  browser_evaluate(() => document.getElementById(section_id).scrollIntoView())
  browser_run_code(async (page) => await page.waitForTimeout(1500))
  browser_take_screenshot(filename: "tmp/playwright/sample{N}/{순번}-{section}.png")
```

### 10.4 Playwright 동영상 녹화 (WebM) (사용자 요청 시에만)

애니메이션 시연을 동영상으로 녹화하고 싶은 경우, 사용자가 명시적으로 요청하면 `browser_run_code`를 통해 WebM 비디오 녹화가 가능하다.
설치 절차 및 GIF/MOV 변환 방법은 `tools/tools-window.md`를 참조한다.

⚠️ **주의**: 이 프로젝트는 Windows 환경 기준이다. ffmpeg 설치 시 OS를 판별하여 호환성을 고려할 것 (npm `ffmpeg-static` 패키지는 OS별 바이너리를 자동 선택하므로 권장).

---

## 11. WPF App 마이그레이션 워크플로우 (XAML → Blend 편집용)

수집한 WPF XAML 애니메이션을 **Blend for Visual Studio**에서 열람/편집 가능한 형태로 변환하는 워크플로우.
목적은 다른 플랫폼에서 애니메이션을 셀프 구현할 때 Blend 타임라인으로 키프레임/이징을 시각적으로 확인하는 것이다.

### 11.1 핵심 참조 문서

변환 작업 전 반드시 이 가이드를 읽고 규칙을 따른다:

```
design-wpf-app/docs/animation-migration-guide.md
```

이 문서에 다음 내용이 정의되어 있다:
- Window → UserControl 변환 규칙
- ControlTemplate 해체 → 루트 Grid 직접 배치
- Transform에 x:Name 부여 (인덱스 사용 금지)
- Storyboard 이중 구조 (Resources x:Key + EventTrigger 인라인)
- DemoSequence 의도 기반 설계 프로세스
- 이벤트 트리거 주의사항
- 변환 체크리스트 8단계

### 11.2 프로젝트 구조

```
design-wpf-app/
├── design-wpf-app.slnx          ← Blend에서 이 파일을 열어 작업
├── MainWindow.xaml               ← 좌측 네비게이션 + 우측 컨텐츠 뷰어
├── MainWindow.xaml.cs            ← CreateSampleControl() 에 신규 항목 등록
├── migrated/                     ← 변환된 UserControl XAML
│   ├── Sample{NN}_{PascalName}.xaml
│   └── Sample{NN}_{PascalName}.xaml.cs
├── db/
│   ├── migration-db.json         ← 마이그레이션 현황 DB (v2 스키마)
│   └── README.md
└── docs/
    └── animation-migration-guide.md  ← 변환 핵심 지침
```

### 11.3 워크플로우 단계

```
Step 1. 원본 XAML 분석
  → design/xaml/sample/{NN}-{name}.xaml 읽기
  → 트리거/속성변화/이징 파악 (5가지 질문 — 가이드 참조)
  → 애니메이션 유형 판별 (인터랙션형/자동반복형/시퀀스형/코드생성형)

Step 2. UserControl 변환
  → animation-migration-guide.md 의 변환 체크리스트 8단계 수행
  → migrated/Sample{NN}_{PascalName}.xaml + .xaml.cs 생성
  → Resources에 개별 Storyboard x:Key 정의
  → DemoSequence: 원본 의도 분석 → Phase 매핑 → 보강 연출 추가
  → EventTrigger에 인라인 Storyboard 복제

Step 3. MainWindow 등록
  → MainWindow.xaml.cs의 CreateSampleControl() switch에 신규 항목 추가

Step 4. 빌드 확인
  → dotnet build 실행, 오류 0 확인

Step 5. DB 업데이트
  → db/migration-db.json 항목 업데이트:
    - status: "빌드성공"
    - animationSummary: 사용자 관점 시나리오 한 줄 요약
    - coreAnimations: 핵심 기법 배열
    - triggerType: 발동 유형
  → migrationLog에 변환 로그 추가 (핵심기능 제목 요약 포함)
```

### 11.4 Blend 활용 팁

- `design-wpf-app/design-wpf-app.slnx`를 Blend for Visual Studio에서 열면 바로 작업 가능
- 각 UserControl의 **타임라인 드롭다운**에서 Storyboard 선택 가능
- `DemoSequence`를 선택하고 재생(▶)하면 전체 애니메이션 흐름을 한 번에 확인
- 개별 Storyboard 선택 시 키프레임 편집/이징 수정 가능
- 디자인 서피스에서 마우스 인터랙션으로 런타임과 동일한 동작 확인

### 11.5 배치 변환 전략

27개 이상의 XAML을 효율적으로 변환하는 전략:

```
Phase 1: 첫 1개 변환 + 빌드 확인 (프로토타입)
Phase 2: 유사 형태 5개씩 배치 변환 + 빌드
Phase 3: 특이 형태 개별 변환 + 빌드
Phase 4: 전체 DB 업데이트 + 사용자 피드백
```

- 자동반복형(Loaded+Forever)은 가장 단순 — 배치 처리에 적합
- 인터랙션형(호버/클릭)은 투명 Button 히트 영역 패턴 적용
- 코드생성형(동적 요소)은 개별 주의 필요
- Agent 도구로 4~5개를 병렬 생성하면 속도 향상

---

## 12. 펜슬 창 복원 (윈도우 버그 대응)

펜슬 프로세스는 구동 중인데 창이 화면 밖(이전 모니터 좌표 등)에 있어 보이지 않거나 MCP 응답이 비정상일 때 사용한다.
멀티모니터 분리/해상도 변경 후 자주 발생하며, 윈도우 작업표시줄에서도 복원이 안 되는 경우가 있다.

### 12.1 발동 조건

다음 중 하나라도 해당되면 §12.2 절차를 우선 수행한다:
- 사용자가 "펜슬 창이 안 보임", "복원 안 됨", "창이 사라짐" 등 호소
- `get_editor_state` 응답이 비어있거나 시간 초과
- `get_screenshot`이 빈 화면/잘린 영역만 반환
- 사용자가 "펜슬은 구동된 상태" 라고 명시했지만 MCP가 응답하지 않음

### 12.2 복원 절차

```bash
# Bash 도구로 실행 (Git Bash 환경)
powershell.exe -ExecutionPolicy Bypass -File "tools/RESTORE/find-pencil.ps1"
```

스크립트 동작:
1. `EnumWindows`로 타이틀에 `.pen`을 포함한 모든 창 검색 (펜슬 창 타이틀은 파일명 형태)
2. 현재 커서가 있는 모니터의 작업영역 중앙으로 이동 + `ShowWindow(SW_RESTORE)`
3. 크기를 `min(1200, 화면80%) × min(800, 화면80%)`로 정규화
4. `%APPDATA%\Pencil\config.json`의 좌표/크기를 같은 값으로 갱신 (다음 실행 시 재발 방지)

### 12.3 검증

복원 후 펜슬 MCP 호출이 정상 동작하는지 확인한다:
```
get_editor_state({ include_schema: false })
  → 응답 정상 → §2 워크플로우 진행
  → 응답 비정상 → 사용자에게 펜슬 재시작 요청
```

### 12.4 안티패턴

| 안티패턴 | 결과 | 올바른 방법 |
|----------|------|-------------|
| 펜슬 강제 종료 후 재실행 권장 | 작업 중 .pen 변경 사항 손실 위험 | 우선 §12.2 복원 시도, 실패 시에만 재시작 |
| 스크립트 검색어를 "Pencil"로 변경 | 창 타이틀이 파일명(`*.pen`)이라 매칭 실패 | 검색어 `.pen` 유지 |
| config.json 직접 편집 | 펜슬이 실행 중이면 덮어쓰기됨 | 스크립트가 처리하므로 수동 편집 불필요 |
| 무한 루프 복원 시도 | 펜슬 미구동 시 무의미 | 1회 시도 후 미발견이면 사용자에게 펜슬 실행 요청 |

### 12.5 스크립트 위치

```
tools/RESTORE/
├── find-pencil.ps1       ← 창 복원 (이 섹션에서 사용)
└── archive-project.ps1   ← 프로젝트 아카이브 (별도 용도)
```

---

## 13. 이미지 생성 / 편집 (Gemini · ComfyUI Z-Image-Turbo)

펜슬 카드의 삽화·배경, HTML 페이지의 히어로 이미지, 디자인 레퍼런스 등 이미지가 필요할 때 사용한다.
프로바이더 추상화 구조로 **Gemini**(클라우드, generate + edit)와 **ComfyUI Z-Image-Turbo**(로컬, generate only)를 지원한다.

### 13.1 기본 설정

| 항목 | 값 |
|------|-----|
| 시크릿 파일 | `.secret/gemini.json` (저장소 루트 기준, ComfyUI는 불필요) |
| 기본 프로바이더 | `gemini` |
| 이미지 저장 루트 | `image/` (저장소 루트 기준) |
| 저장 패턴 | `image/{provider}/{YYYY-MM-DD}-{topic}.png` |
| 스크립트 | `.claude/skills/pencil-design/scripts/image-gen.py` |

> 환경변수 `GEMINI_SECRET_PATH`, `IMAGE_GEN_ROOT`로 경로 override 가능.
> 시크릿 형식: `{ "api_key": "...", "image_model": "gemini-3.1-flash-image-preview" }`

### 13.2 프로바이더 비교

| 항목 | Gemini | ComfyUI Z-Image-Turbo |
|------|--------|----------------------|
| 호출명 | `--provider gemini` | `--provider comfyui` 또는 `z-image` / `z-image-turbo` |
| 위치 | 클라우드 (Google API) | 로컬 (`http://192.168.0.64:8188`) |
| API 키 | 필요 (`.secret/gemini.json` → `api_key`) | 불필요 |
| 모델 | gemini-3.1-flash-image-preview | Z-Image-Turbo BF16 (4 step) |
| 이미지 편집 | O (generate + edit) | X (text-to-image only) |
| 비율 지원 | 자유 | 1:1, 16:9, 9:16, 4:3, 3:4 |
| 속도 | 네트워크 의존 | 로컬 GPU, 약 5–15초 |
| 주의 | 일일 쿼터 | ComfyUI 서버 실행 필요 |

### 13.3 CLI 사용법

> ⚠️ **Windows 환경 — 반드시 `py` 사용**
> Windows의 `python` 명령은 보통 Microsoft Store 스텁(exit 49, "Python"만 출력)을 가리켜 **조용히 실패**한다. 백그라운드 실행 시 디버깅이 매우 어렵다. **반드시 Python Launcher `py`를 사용**한다. 본 문서의 예제는 호환을 위해 `python` 으로 표기되어 있으나, Windows에서는 `py`로 치환 실행한다.

#### 이미지 생성 (Gemini)

```bash
py .claude/skills/pencil-design/scripts/image-gen.py generate \
  --prompt "A friendly robot reading a book in a cozy library" \
  --topic robot-reading \
  --provider gemini \
  --aspect-ratio 16:9
```

#### 이미지 생성 (ComfyUI Z-Image-Turbo, 로컬)

```bash
py .claude/skills/pencil-design/scripts/image-gen.py generate \
  --prompt "A futuristic cityscape at sunset, cyberpunk style" \
  --topic cyber-city \
  --provider comfyui \
  --aspect-ratio 16:9
```

`--provider` 별칭: `comfyui`, `z-image`, `z-image-turbo` 모두 동일.

#### 이미지 편집 (Gemini only)

```bash
py .claude/skills/pencil-design/scripts/image-gen.py edit \
  --prompt "배경을 석양이 지는 해변으로 바꿔줘" \
  --input-image image/samsung.jpg \
  --topic samsung-sunset \
  --provider gemini
```

> ComfyUI Z-Image-Turbo는 text-to-image 전용이므로 edit 미지원. 편집은 gemini 사용.

#### 출력 형식

성공 시:
```json
{"status": "ok", "path": "image/gemini/2026-05-06-robot-reading.png", "provider": "gemini"}
```

실패 시:
```json
{"status": "error", "message": "에러 메시지"}
```

### 13.4 워크플로우

```
Step 1. 프롬프트 준비
  → 사용자 요청에서 이미지 설명 추출
  → 영문 프롬프트가 품질이 더 좋음 (한글도 지원)

Step 2. 이미지 생성
  → image-gen.py generate 실행 (Windows: py)
  → JSON 출력에서 path 확인

Step 3. 결과 확인
  → Read 도구로 시각적 확인 (PNG 표시 지원)
  → 만족스럽지 않으면 프롬프트 수정 후 재생성

Step 4. (선택) 펜슬 / HTML 통합
  → 펜슬 카드 삽화: batch_design의 G() 오퍼레이션으로 frame에 이미지 삽입
  → HTML 페이지: design/xaml/output/sample{N}/ 의 <img src="..."> 또는 background-image
```

### 13.5 ComfyUI 서버 요구사항

- 엔드포인트: `http://192.168.0.64:8188`
- 서버 시작: `cd C:\Users\psmon\ComfyUI && .\venv\Scripts\Activate.ps1 && python main.py --listen 0.0.0.0 --port 8188`
- 서버 미실행 시 에러 메시지에 시작 방법 안내가 포함됨

### 13.6 프로바이더 구조

```
.claude/skills/pencil-design/scripts/
├── image-gen.py              ← 메인 CLI
└── providers/
    ├── __init__.py           ← 프로바이더 레지스트리
    ├── gemini_provider.py    ← Gemini 구현 (클라우드, .secret/gemini.json)
    └── comfyui_provider.py   ← ComfyUI Z-Image-Turbo (로컬, 키 불필요)
```

향후 프로바이더 추가 시:
1. `providers/{name}_provider.py` 생성 (인터페이스: `generate`, `edit`)
2. `providers/__init__.py`의 `get_provider()`에 등록
3. `--provider {name}`으로 호출

### 13.7 의존성 설치

```bash
# Gemini provider — generate (필수)
py -m pip install google-genai

# Gemini provider — edit (선택, 이미지 편집 시 필요)
py -m pip install Pillow

# ComfyUI provider — 추가 의존성 없음 (urllib만 사용)
```

설치 확인:
```bash
py -c "from google import genai; print('genai OK')"
py -c "from PIL import Image; print('PIL OK')"
```

### 13.8 안티패턴

| 안티패턴 | 결과 | 올바른 방법 |
|----------|------|-------------|
| API key를 스킬/스크립트에 하드코딩 | 보안 위험 | `.secret/gemini.json` → `api_key` 자동 로딩 |
| 키를 여러 파일에 중복 보관 | 동기화 누락 | 서비스당 `.secret/{service}.json` 하나만 사용 |
| JSON 출력 미파싱 후 경로 추측 | 에러 시 잘못된 경로 사용 | `status` 필드 확인 후 `path` 사용 |
| 한글 파일명 사용 | OS 호환성 문제 | 영문 topic 키워드 사용 |
| 이미지 확인 없이 바로 게시 | 품질 미검증 | Read 도구로 시각적 확인 후 게시 |
| ComfyUI에 edit 호출 | NotImplementedError | 편집은 gemini provider 사용 |
| ComfyUI 서버 미확인 후 생성 시도 | 타임아웃/연결 오류 | 서버 상태 먼저 확인 또는 에러 안내 참조 |
| Windows에서 `python` 명령 사용 | MS Store 스텁이 조용히 실패 (exit 49) | **반드시 `py` 사용** |
