# 정합성·복구 업데이트 (2026-11-09)

기준: GameServer 7fd5d04857e341e08ee6fd3ecf22c790286c7b21 + 작업 시작 당시 로컬 변경.
APM 검증 기준: llm-local-apm 8eecd7d3b9bfb9c2afef179a4d41bb60bea1b1b8.
기존 ScenarioClient와 docker-compose.yml의 미커밋 변경은 유지했다.

## 코드별 변경

| 위치 / 함수 | 변경 동작 |
|---|---|
| PlayerGrain.ExecuteProgressAsync | 세션 소유권·극장 상태 확인, 검증, 진행 변경을 동일한 non-reentrant Grain 호출에서 직렬화한다. Handler의 락 확인과 실제 저장 사이 경합을 막는다. |
| ProgressCommands.UnlockBoardAsync | 소유·마스터·기해금·payload를 검사한다. Redis에서 잔액 확인/차감/해금/전투력/dirty를 함께 변경한다. |
| ProgressCommands.EnhanceAsync | 두 재화의 잔액을 모두 검사한 뒤 한 Lua 실행으로 변경한다. 소유권·상한 검사와 등차수열 비용 계산으로 조작된 목표 레벨의 긴 반복도 막는다. |
| ProgressState.EnhancementDelta / BoardDelta | 캐릭터별 반올림된 최종 전투력의 전후 차이를 구한다. 전체 보드는 보유 캐릭터 각각에 적용해 합산한다. |
| ProgressState.AcquisitionPower | 신규 캐릭터에도 이미 해금한 전체 보드 효과를 적용한다. |
| ProgressRepository.LoadAsync | 진행 상태 최초 로드/오류 후 재로드 시 보유·레벨·보드로 파생 전투력을 검산한다. |
| ProgressRepository.BeginGachaAsync / RecoverGachaAsync | 확정 추첨 결과를 차감과 함께 Redis에 기록한다. SQL 획득을 멱등 실행하고 Redis 보상·전투력·캐시·영수증을 마무리한다. |
| SessionFlush.FlushOneAsync | 재화·전투력·레벨·보드를 하나의 Npgsql 트랜잭션으로 저장한다. 커밋 성공 후에만 dirty를 제거한다. |
| ProgressFlushWorker.RunAsync | 시작 시 + 30초마다 dirty 플레이어를 최대 4명씩 Grain을 통해 재시도한다. |
| ClientSession.DisconnectAsync | 한 종료 Task를 공유한다. 소켓/플레이어 인덱스를 먼저 정리하고 각 cleanup을 독립 수행한다. 완료까지 세션 목록에 남아 shutdown이 기다린다. |
| PlayerGrain.OnDisconnectAsync / SessionManager.UnregisterPlayerId | SessionId가 일치할 때만 현재 소유권/인덱스를 제거한다. 길드·매칭에도 세션 소유권을 둔다. |
| ReconnectHandler.HandleAsync | 1회 사용 토큰을 원자적으로 소비하고 다음 재접속 토큰을 응답한다. |
| PlayerRepository.GetOrCreateByNameAsync | 계정/캐릭터 상태/초기 엘리프를 같은 SQL 트랜잭션에서 생성한다. |
| Repository 캐시 초기화 | SQL에서 읽은 오래된 값이 최신 Redis 값을 덮어쓰지 않도록 최초 값만 삽입하고 다시 읽는다. |
| Program / HeartbeatManager | heartbeat/flush Task를 추적하고 종료 정리 후 Orleans를 중지한다. 실패한 heartbeat 스윕을 재시도한다. |

HP 증가량 6을 5회 적용할 때 매번 반올림하면 HP 기여분이 5지만 최종 증가량 30의 기여분은 3이다.
이제 최종 스탯의 전후 차이를 사용하므로 단계별·일괄 강화가 일치한다.
과거 잘못 차감된 재화를 추정해 환급하지는 않는다. 전투력은 파생 값만 재검산한다.

## 패킷과 설정

- 기존 PacketId와 보드/강화/가챠 결과 형식은 유지한다.
- GachaRequest: 기존 빈 payload 허용. 멱등 재시도를 원하는 클라이언트는 16바이트 요청 ID를 보낸다.
  C#은 Guid.ToByteArray(). 응답이 불명확하면 **동일한 ID**로 재시도한다.
  같은 플레이어/ID의 완료 응답은 7일간 저장된다. 빈 payload 또는 7일 이후에는 이 보장이 없다.
- ReconnectResult: 실패는 [1B 0], 성공은 [1B 1][36B 다음 토큰 문자열].
  반복 재접속 클라이언트는 새 토큰을 보관해야 한다.
- 이전 연결의 진행 변경은 Grain 안에서도 거부한다. 인증된 소켓의 재로그인/재접속 요청은 연결을 종료한다.
- 보드·강화·가챠의 잘못된 payload는 실패 결과를 반환한다.
- TRICKCAL_MAX_CHARACTER_LEVEL: 기본 1000, 설정 범위 1~1,000,000.
- TRICKCAL_ENABLE_DEBUG_COMMANDS=1일 때만 기존 APM 오류 재현용 재화 지급을 켠다.
  기본은 실패 응답이다. 켜면 비관리자 지급 후 예외를 발생시키는 기존 **의도적 결함**도 유지된다.
- TRICKCAL_ENABLE_FAULT_INJECTION=1과 TRICKCAL_GACHA_FAULT=after-reserve 또는 after-sql을
  함께 지정하면 해당 지점에서 예외를 발생시킨다. 일반 실행에서는 지정하지 않는다.

## 복구 기록과 영속성

새 Redis 키:
- trickcal:dirty_players
- player:gacha:pending:{playerId}
- player:gacha:receipt:{playerId}:{requestId:N} (TTL 604800초)

새 SQL 테이블이나 기존 데이터 초기화는 필요 없다.
Redis가 진행 상태와 복구 기록을 보존한다는 조건에서 게임 서버 프로세스 재시작을 복구한다.
Redis 자체 데이터 유실까지 해결하는 분산 트랜잭션은 아니다.

새 로컬 스택에는 다음 영속성 override를 적용할 수 있다.

~~~powershell
docker compose -f docker-compose.yml -f docker-compose.reliability.yml up -d postgres redis
~~~

AOF, everysec, noeviction, named volume을 사용한다. everysec은 OS/전원 장애 시 마지막 약 1초의
쓰기 손실 가능성을 남긴다. 기존 컨테이너를 새 볼륨으로 재생성하기 전에는 현재 데이터를 보존/이관한다.
이번 작업에서는 기존 컨테이너나 원래 DB를 재시작·초기화하지 않았다.
Redis Cluster의 hash-slot 분산은 검증 대상이 아니다.

## 검증 실행

외부 테스트 프레임워크 없이 실행하는 tests/GameServer.Checks를 추가했다. 실패 시 종료 코드는 1이다.
GameServer의 기존 방식대로 형제 디렉터리에 apm/src/Apm.Agent가 필요하다.

~~~powershell
# 순수 계산 검증
./tools/verify.ps1

# 빈 PostgreSQL DB와 비어 있는 전용 Redis DB 2개를 먼저 준비
$env:TRICKCAL_TEST_POSTGRES = 'Host=127.0.0.1;Port=5433;Database=trickcal_checks_local;Username=postgres;Password=password'
$env:TRICKCAL_TEST_REDIS = '127.0.0.1:6380,defaultDatabase=14'
$env:TRICKCAL_TEST_CRASH_REDIS = '127.0.0.1:6380,defaultDatabase=15'
./tools/verify.ps1 -Integration -CrashRecovery
~~~

검증기는 trickcal_checks_로 시작하는 **빈 SQL DB**와 **빈 Redis DB**만 허용한다.
스키마/시드는 검증기가 적용하며 기존 데이터를 지우지 않는다. 재실행 시 새 DB를 준비한다.
.verification/ 및 logs/는 로컬 산출물로 Git에서 제외한다.

검증 내용:
- 고정 seed 무작위 성장 순서 200 × 100회. 별도 정수 공식과 전투력을 비교
- 동일 보드 동시 요청 24개 중 성공/차감 정확히 1회
- 미보유/비정상 목표/잘못된 payload 거부, 골드 부족 시 마카롱 차감 없음
- 전체 보드 및 신규 획득, 극장 중 변경 거부와 변조 값 복구
- 동일 가챠 ID 동시 요청 12개, 차감·SQL 이후 주입 오류 복구
- SQL 후반 FK 오류 시 앞선 재화 저장까지 롤백, dirty 유지 후 실제 worker 재저장
- 반복 disconnect, 교체된 세션 보호, DB 연결 장애 중 소켓/인덱스 정리
- 실제 TCP 로그인/토큰 재발급/사용한 토큰 거부/가챠 재전송
- Python 검사: 실제 GameServer에서 SQL 저장 뒤 오류 주입 → 프로세스 kill → 새 Orleans cluster
  identity로 재기동 → 동일 요청의 추첨·재화·전투력 확인

로컬 결과: .NET SDK 10.0.301, PostgreSQL 18.4, Redis Windows 3.0.504에서
**40,287 assertions 통과**, 실제 서버 kill/restart 검사 통과.
GitHub Actions에는 PostgreSQL 16 / Redis 7 / .NET 10 및 고정 APM commit을 지정했다.
원격 CI 실행은 아직 하지 않았으며 Redis 7은 이번 로컬 실행 환경이 아니다.
최초 restore의 NuGet 취약점 조회는 네트워크 제한으로 NU1900을 냈다. 후속 로컬 빌드는
복원된 패키지와 NuGetAudit=false 복원을 사용했다. 보안 감사 성공을 의미하지 않는다.

## 기존 APM 및 후속 범위

기존 APM의 request/span, pseudonym, DB/Redis/Orleans/send-lock 계측을 유지한다.
APM 저장소는 수정하지 않았다. README에서 로컬 APM과 OTLP→Elastic 수신 상태를 구분했다.

이번 작업은 정합성과 복구 검증이다. 변경 후 3만 동접 성능, APM ON/OFF 비용,
다중 silo 장애 전환, Redis 프로세스/호스트 장애, Elastic 수신, APM durable replay와
LLM 정량 평가를 완료한 것은 아니다. 이름 기반 데모 로그인은 그대로다.


## 후속: 매칭 결과 계측 보완 (2026-11-09)

시범 운영에서 매칭은 성공했지만 MatchRequest 20건이 unknown으로 수집됐다.
MatchResult는 Orleans OneWay observer로 나중에 전송되므로 요청 Handler의 Activity와 다른 실행 문맥이다.

- IMatchGrain.RequestMatchAsync가 MatchRequestStatus를 반환하고 MatchHandler가 결과를 명시한다.
  match_queued / match_already_queued / match_created는 요청 처리 성공,
  match_stale_session / match_notification_unavailable은 거부다.
  대기열 접수 성공은 매칭 완료를 의미하지 않는다.
- MatchGrain은 플레이어별 SessionId와 최초 요청의 traceparent를 보관한다.
  같은 세션의 중복 대기는 최초 원인을 덮어쓰지 않는다. 취소/매칭 시 기록을 제거한다.
- PlayerGrain.SendMatchResultAsync가 대상 세션을 확인한 후 observer로 전달한다.
- PlayerSessionObserver.DeliverMatchResultAsync는 독립 MatchResult background trace를 생성하고
  원래 MatchRequest의 trace/span을 LinkedTraceId/LinkedSpanId로 연결한다.
  실제 TCP WriteAsync 성공 뒤에만 success를 기록하고 전송 예외도 기록한다.
  TCP 쓰기 완료는 클라이언트 애플리케이션의 별도 ACK까지 보장하지 않는다.
- observer의 ambient Activity는 수집되지 않은 Orleans 부모를 참조할 수 있으므로
  알림 scope 동안 비우고 finally에서 복원한다. 첫 보완 실행의 부모 누락을 실제 APM API 검사로 발견해 수정했다.
- 클라이언트 패킷 형식은 그대로다. Orleans RPC 인터페이스가 바뀌므로 같은 cluster에서 구버전·신버전을 혼용하지 않는다.

회귀 검사에 대기·중복·취소·세션 보호·각 플레이어의 최초 요청 연결을 추가했다.
전체 솔루션 빌드 오류/경고 0, 콘솔 검사 40,298 assertions 통과.
최종 20명 시범 실행은 trickcal-matchfix-final-20261109-224543으로 구분한다.
대기열 접수 10건 + 매칭 생성 10건, 결과 전송 20건 및 원 요청 연결 20건을 확인했다.
unknown 0건, 각 알림 trace의 부모 누락/잘림 0건이다. 이전 시범 및 중간 실패 기록은 보존했다.

APM 상세 수집을 100%로 설정하고 짝수 인원으로 각 1회 매칭하는 시나리오를 실행한 뒤 다음 읽기 전용 검사를 쓴다.

~~~powershell
python tools/check_match_telemetry.py --collector http://127.0.0.1:5219 --service Trickcal-Pilot --run <APM_RUN_ID> --players 20
~~~

APM 화면에서 MatchRequest 상세의 결과 코드로 접수/생성을 구분한다.
MatchResult 상세의 '이 작업을 요청한 추적' 링크로 해당 플레이어의 원 요청을 확인할 수 있다.


## 후속: APM 비용·코드 전후 실측 (2026-11-10)

[게임 종료 후 3만 동접, 네 조건 × 3회 측정](apm-ab-comparison-20261110.md)을 완료했다.
APM 비용과 게임 코드 변경 차이, 수집 대조, 긴 지연 및 재시도, 재현 조건을 따로 기록했다.
이 문서의 이전 미측정 범위 표기는 당시 상태다. 정합성·수집 신뢰성을 속도 향상률로 바꾸어 해석하지 않는다.


## 빌드 버전과 CI 게시 상태 (2026-11-10)

큐·디스크 재전송 보완을 포함한 APM 커밋 `027d8d87cd9eec983521cbf767befb8c5ffc8075`가 이번 공개 서버의 검증 기준이다.
위 최초 검증 기준은 당시 이력으로 보존하며, [APM의 실제 운영 기여](apm-operational-impact-20261110.md)에서 코드와 실행별 공개 집계를 연결한다.

같은 APM SHA를 고정한 CI 워크플로는 로컬에 준비했다. 현재 게시 인증의 워크플로 권한 제한으로 해당 파일은 이번 푸시에서 제외하고 로컬에 보존했다.
원격 CI는 아직 게시·실행하지 않았으며, 공개된 `tools/verify.ps1`과 각 검사 프로젝트로 로컬 검증을 재현한다.
