# LLM Docs Index / LLM 문서 색인

> Tutorials and deep-dives for embedding LLMs in AgentZero Lite (and .NET apps in general).
> AgentZero Lite 및 .NET 앱 일반에 LLM을 내장하는 튜토리얼·심화 자료 모음.

---

## Tutorials (beginner-friendly narratives) / 튜토리얼 (스토리 형식)

| # | Topic | 주제 | 🇰🇷 한국어 | 🇺🇸 English |
|---|---|---|---|---|
| 01 | Embedding Gemma 4 on-device in .NET — journey from failure to success | .NET에 Gemma 4 온디바이스 탑재 — 실패와 성공의 여정 | [`ko/gemma4-ondevice-tutorial.md`](ko/gemma4-ondevice-tutorial.md) | [`en/gemma4-ondevice-tutorial.md`](en/gemma4-ondevice-tutorial.md) |

---

## Reference articles (deep-dive, single-language) / 참조 아티클 (심화, 단일 언어)

기술 재료 원본. 튜토리얼이 이들을 이야기로 재구성.

The raw technical material. Tutorials above reconstruct these as narratives.

| Document | Scope |
|---|---|
| [`../resaerch-geema4.md`](../resaerch-geema4.md) | Full research & implementation log — commit pinning, ZeroCommon integration, multi-turn session, LlamaSharp 0.26 API notes |
| [`../gemma4-gpu-load-failures.md`](../gemma4-gpu-load-failures.md) | Crash case catalogue (7 cases): iGPU selection, VK_KHR_shader_bfloat16, env var CRT propagation, etc. — with symptom → cause → mitigation → verification signal |
| [`../gemma4-performance-benchmarks.md`](../gemma4-performance-benchmarks.md) | Performance benchmark matrix: E4B vs E2B × CPU vs Vulkan × COOPMAT / NoKqvOffload / FA combinations |

---

## How to read this section / 읽는 순서 제안

**For developers new to on-device LLM / 온디바이스 LLM 처음 접하는 분**:
1. Pick your language tutorial (01) above / 언어에 맞는 튜토리얼(01) 선택
2. Work through the CPU path first (sections 1–6) / CPU 경로(1–6절)부터 따라 하기
3. Move to Vulkan (sections 7–9) only if CPU works end-to-end / CPU 경로가 통한 뒤 Vulkan(7–9절)으로 확장

**For contributors hitting a new crash / 새 크래시 만난 기여자**:
1. Check `gemma4-gpu-load-failures.md` first — chances are it's already catalogued / 크래시 케이스가 이미 정리돼 있을 확률 높음
2. If not, use `LlmProbe` subprocess to isolate the failure / 새 케이스면 `LlmProbe` 서브프로세스로 격리
3. Add a new failure case to the catalogue following the existing template / 템플릿대로 케이스 추가

**For perf-tuning / 성능 튜닝**:
1. Read `gemma4-performance-benchmarks.md` methodology first / 먼저 벤치마크 방법론 숙지
2. Run `LlmProbe.exe <backend> bench` to measure before/after / 변경 전후를 probe bench로 측정
3. Update the matrix with your findings / 결과를 매트릭스에 추가

---

## Planned topics / 예정 주제

이 섹션은 LLM 관련 문서가 쌓이는 위치. 향후 추가될 가능성:

This section grows as more LLM docs land. Likely additions:

- ⏳ Local RAG with Gemma embeddings / Gemma 임베딩 기반 로컬 RAG
- ⏳ Whisper.net + Gemma dual-GPU routing (iGPU + dGPU) / Whisper.net과 Gemma 이원 GPU 활용
- ⏳ GBNF grammar-constrained JSON output / GBNF 문법으로 JSON 출력 강제
- ⏳ Integrating on-device LLM into AgentBot core UI / AgentBot 본 UI에 온디바이스 LLM 통합
- ⏳ Migration guide when LLamaSharp 0.27+ NuGet ships Gemma 4 natively / LLamaSharp 0.27+ 공식 지원 후 마이그레이션 가이드

---

## File layout / 파일 구조

```
Docs/llm/
├── index.md                            ← 이 파일 / this file
├── ko/
│   └── gemma4-ondevice-tutorial.md     ← 한국어 튜토리얼
└── en/
    └── gemma4-ondevice-tutorial.md     ← English tutorial

Docs/                                   ← parent folder holds raw reference docs
├── resaerch-geema4.md
├── gemma4-gpu-load-failures.md
└── gemma4-performance-benchmarks.md
```

**Convention for future docs / 새 문서 규약**:
- Story/narrative tutorials go under `ko/` + `en/` as mirrored pairs
- Deep-dive / reference docs stay in `Docs/` root with a single language (whichever the author was writing in). Link from this index with a short English scope summary so non-Korean readers can decide if translation is worth asking for.
- Filename pattern: `<topic>-<flavour>.md` (flavour = `tutorial` / `reference` / `benchmark` / `failures` etc.)
- Add entry to the appropriate table above when landing a new doc.

튜토리얼은 한/영 쌍으로 mirror. 심화 자료는 한 언어만이라도 루트에 두고 이 index에서 요약(영문)으로 소개 — 필요 시 번역 요청 가능하게.
