# Trickcal-Bottleneck-Server

에피드게임즈의 트릭컬 리바이브에서 발생할 수 있는 서버 병목 현상을 예상하고 해결 방안을 설계해보는
프로젝트입니다. 트릭컬 리바이브의 실제 서버 로직과는 무관하며, 서버 프로그래밍의 설계와 구조를
익히기 위한 개인 학습용 리포지토리입니다.

목적은 콘텐츠 폭 확장이 아니라 **"게임의 병목을 지우는 것"** — 복잡한 로직이 필요해지는 결투장·
랭크업 시스템 등을 제외함.

## 설계 문서 (이 저장소)

- @docs/design-plan-draft.md — DB 스키마 · 전투력 공식 · 보드 시스템 · 세션(스토리 락, redis→PG flush) 아키텍처. **최신 확정 소스 (rev.3, 2026-08-07)**
- @docs/server-arch-note.md — 최초 문제 진단과 논의 기록(2026-08-05). design-plan-draft.md 이전 단계 자료, 배경 참고용

## 이전 프로젝트 (Game-Server-CSharp) — 기반 컨텍스트

이 프로젝트는 `C:\Users\mocha\Desktop\Game-Server-CSharp`(C# / .NET 10 / Orleans / PostgreSQL / Redis
기반 게임 서버 포트폴리오)의 후속작입니다. 완전히 새 저장소로 시작하지만, 기술 스택과 코딩 컨벤션은
이어받습니다.

- @C:\Users\mocha\Desktop\Game-Server-CSharp\README.md — 이전 프로젝트 구조/기술 스택 개요
- @C:\Users\mocha\Desktop\Game-Server-CSharp\CODING_STYLE.md — 코딩 컨벤션 (이 저장소에서도 동일하게 적용)

## 진행 상태

TCP/Orleans/DB/콘텐츠와 기존 로컬 APM 연동이 구현되어 있다.
2026-11-09 정합성·복구 1차 업데이트의 코드/패킷/검증 기준은
@docs/reliability-upgrade-20261109.md 를 먼저 확인한다.
기존 설계 문서는 배경 자료이며 위 업데이트가 설명하는 실제 구현 변경을 함께 적용한다.
원래 DB와 APM 근거 데이터를 초기화하지 않는다.
성능 설명은 @docs/apm-ab-comparison-20261110.md 의 게임 종료 후 APM ON/OFF × 코드 전후 실측을 기준으로 하고, 정합성 업데이트를 속도 향상률로 표현하지 않는다.
