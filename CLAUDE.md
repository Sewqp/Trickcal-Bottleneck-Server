# Trickcal-Bottleneck-Server

에피드게임즈의 트릭컬 리바이브에서 발생할 수 있는 서버 병목 현상을 예상하고 해결 방안을 설계해보는
프로젝트입니다. 트릭컬 리바이브의 실제 서버 로직과는 무관하며, 서버 프로그래밍의 설계와 구조를
익히기 위한 개인 학습용 리포지토리입니다.

목적은 콘텐츠 폭 확장이 아니라 **"게임의 병목을 지우는 것"** — 이 기준으로 범위(진화 시스템 제외 등)를 판단합니다.

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

DB 스키마 · 전투력 공식 · 아키텍처 설계는 확정됨(위 문서 참고). 실제 구현은 아직 시작 전 — 저장소
초기화(git init) 및 설계 문서 반영까지만 완료된 상태.

나머지 미정 항목(인프라 재사용 범위, 패킷/API 스펙, flush 대상 redis→PG 매핑, 동시성 처리, 타임아웃
구체값 등)은 실제 구현을 진행하면서 그때그때 결정합니다 — 미리 채워 넣지 말 것.
