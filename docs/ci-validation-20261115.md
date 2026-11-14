# Ubuntu CI 실행·복구 검증

2026-11-15 KST에 GitHub Actions의 `Server correctness and recovery`를 실제 실행했다.

- [검증 실행 및 전체 로그](https://github.com/Sewqp/Trickcal-Bottleneck-Server/actions/runs/43609555599)
- 검증 커밋: `83bdc177e469ad26424983d52be16dcca934b3fc`
- [워크플로](../.github/workflows/server-checks.yml), [정합성 검사](../tests/GameServer.Checks/Program.cs), [실제 프로세스 복구 검사](../tools/check_crash_recovery.py)
- 환경: GitHub 호스팅 Ubuntu 26.04.1, .NET SDK 10.0.404, PostgreSQL 16, Redis 7
- APM 참조: `Sewqp/llm-local-apm`의 `027d8d87cd9eec983521cbf767befb8c5ffc8075` 커밋 고정

## 실행 결과

| 검사 | 결과 |
|---|---|
| Release 빌드 | 성공, 컴파일 경고 0·오류 0 |
| 계산·저장·Orleans·TCP 통합 | `PASS: 40298 assertions` |
| 실제 GameServer 강제 종료·재시작 | 가챠 확정 결과 재사용, 재화 차감·보상 1회, 전투력 일치 통과 |
| 결과 보관 | `server-check-logs`의 로그 5개를 다운로드해 내용 확인 |

통합 검사는 중복 성장 요청과 소유권, 원자적 재화 변경, 반올림·극장 복구, 가챠 재요청과 주입 장애, SQL rollback과 flush 재시도, 세션 교체·재접속 토큰, 매칭 취소와 인과관계 연결을 포함한다. 40,298은 반복 조건을 포함한 assertion 수이며 독립 테스트 시나리오 수나 사용자 수가 아니다.

프로세스 복구 검사는 SQL 획득 반영 이후 의도적으로 요청을 실패시키고, Redis·PostgreSQL은 유지한 채 게임 프로세스를 강제 종료한다. 다시 시작한 서버에 같은 가챠 요청 ID를 보내 결과·차감·보상을 대조한다. APM 수집 검사는 별도이며 이 복구 프로세스의 계측은 `APM_ENABLED=0`으로 끈다.

## CI에서 재현하는 조건

push·pull request·수동 실행 트리거를 사용한다. 매 실행에서 새 PostgreSQL/Redis 서비스 컨테이너를 만들며 실제 사용자 DB에 연결하지 않는다. 통합 검사는 `trickcal_checks_` 접두사와 빈 DB를 확인한다. 복구 검사는 먼저 통합 검사가 만든 PostgreSQL 자료를 사용하고, Redis는 별도 DB 1의 빈 상태를 확인한다. 검사 코드가 기존 DB를 지우는 방식으로 초기화하지 않는다.

빌드 → 통합 검사 → 프로세스 복구 순서이며 앞 단계 실패를 숨기지 않는다. Bash `pipefail`을 사용하므로 `tee`로 로그를 저장해도 원 명령 실패가 CI 실패로 이어진다. 로그는 성공·실패 모두 수집한다. GitHub artifact 보존 기간은 7일이며 실행 링크만으로 로그를 영구 보존한다고 주장하지 않는다.

첫 실행은 검사에 성공했지만 업로드 도구의 기본 숨김 파일 제외 정책 때문에 `.verification` 로그가 첨부되지 않았다. `include-hidden-files: true`와 `if-no-files-found: error`를 추가한 뒤 다시 실행해 다운로드까지 확인했다. 복구 스크립트의 `APM_ENABLED=false`도 실제 설정이 읽는 값인 `0`으로 수정했다.

## 범위와 한계

이 결과는 격리된 Linux 환경에서 코드의 정합성과 게임 프로세스 장애 복구를 재현한 근거다. 3만 동접 부하를 CI에서 실행한 결과, Redis 자체의 데이터 유실 복구, 운영 배포·롤백 자동화, 모든 플랫폼 호환성 또는 운영 SLA를 증명하지 않는다. 이전 [정합성 검증](reliability-upgrade-20261109.md)과 [부하·APM 비용 실측](apm-ab-comparison-20261110.md)은 각각 당시 환경의 기록으로 유지한다.
