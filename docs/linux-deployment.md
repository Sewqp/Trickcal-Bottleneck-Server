# Linux 단일 서버 배포와 롤백

이 구성은 격리된 Linux 호스트에서 실제 GameServer를 systemd 서비스로 교체하고 되돌리는 재현 도구다. 외부 운영 서버에 배포하거나 DB를 초기화하는 도구가 아니다. 실행 결과는 별도 날짜별 기록에 남긴다.

## 배포 구조

```text
deployment-root/
  releases/<release-id>/   # self-contained linux-x64 publish와 SHA-256 manifest
  current -> releases/... # 현재 릴리스
  previous -> releases/...# 직전 정상 릴리스
  shared/server.env       # DB 연결 등 호스트 설정, mode 0600
  shared/logs/            # 릴리스 교체와 분리한 런타임 로그
  .deploy.lock            # 동시에 진행되는 배포 차단
  events.jsonl           # 활성화·실패·롤백 결과
```

`tools/linux/release.py`는 파일 목록과 SHA-256을 검증한 publish 폴더를 새로운 릴리스 디렉터리에 복사한다. 이미 있는 릴리스 ID를 덮어쓰지 않는다. 활성화 직전 대상과 복구용 이전 릴리스의 파일을 다시 검사한 뒤 `current` 심볼릭 링크를 원자적으로 교체하고 해당 systemd 서비스를 재시작한다.

정상 판정은 단순 포트 열림이 아니다. systemd의 MainPID가 해당 릴리스의 `GameServer`를 실행하는지 `/proc/<pid>/exe`로 확인하고, 전용 테스트 계정의 TCP 로그인 및 하트비트 3회가 성공해야 한다. 후보 버전이 실패하면 기존 `current`를 복원하고 같은 검사를 다시 수행한다. 이때 후보 배포 명령은 종료 코드 1을 반환하여 실패를 숨기지 않는다. 사전 검증 거부나 롤백 자체의 실패는 2다.

systemd는 비특권 사용자로 게임 프로세스를 실행한다. 종료는 SIGTERM, 종료 제한은 20초다. `sudo`는 테스트용 systemd unit 설치·재시작·정리에만 사용한다. 배포 명령은 `trickcal-deploy-*.service` 이름과 해당 배포 루트의 WorkingDirectory/ExecStart를 확인한다.

## 재현

[Linux deployment and rollback 워크플로](../.github/workflows/linux-deploy-checks.yml)는 Ubuntu 24.04에서 실행한다. PostgreSQL 16과 Redis 7은 실행마다 새 서비스 컨테이너로 만든다. DB 이름은 `trickcal_checks_deploy`, Redis는 DB 2이며 비어 있지 않으면 중단한다. 별도 Linux 호스트에서 실행할 때도 **새 DB와 격리된 Redis**를 준비한다. 원본 DB의 내용을 지워 맞추지 않는다.

필요 환경은 systemd, .NET 10 SDK(빌드용), Python 3.11 이상, psql, 비대화형 sudo 권한이다. GameServer 실행물은 `linux-x64 --self-contained true`로 만들어 실행 시 호스트 .NET 설치에 의존하지 않는다. .NET의 Linux 네이티브 의존성까지 없어지는 것은 아니다.

1. 워크플로의 두 `dotnet publish` 명령으로 baseline과 candidate 폴더를 만든다. 이전 소스는 `b802c59673ba267ed16b8d27b00bb7ad979c5c2d`, APM 참조는 `027d8d87cd9eec983521cbf767befb8c5ffc8075`로 고정한다.
2. 각 publish 폴더에 `release.py package <publish-dir> <release-id> <full-source-sha>`로 manifest를 만든다.
3. 격리 DB의 `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, `DEPLOY_TEST_REDIS_PORT`를 지정한다. PostgreSQL 호스트는 `127.0.0.1`, DB 이름은 `trickcal_checks_` 접두사여야 한다.
4. 저장소에서 다음을 실행한다. 출력 디렉터리는 새 경로여야 한다.

```bash
python3 tools/linux/verify_deployment.py \
  .verification/publish-a .verification/publish-b \
  --output .verification/linux-deployment
```

재현기는 빈 DB에 스키마와 시드를 넣고, 실행별로 고유한 systemd unit과 포트를 만든다. 검증이 끝나거나 실패하면 자신이 만든 서비스를 중지하고 해당 unit만 제거한다. 배포 디렉터리와 증거 파일은 남긴다. `shared/server.env`는 비공개 설정 파일이며 GitHub artifact에 포함하지 않는다.

## 검사 순서

1. baseline을 배포하고 로그인·하트비트를 확인한다.
2. 별도 플레이어가 가챠로 캐릭터와 전투력을 얻은 상태에서 연결을 유지한다.
3. candidate를 배포한다. 프로세스 PID가 바뀌고 기존 연결이 종료되는지 확인한다. 같은 플레이어로 재접속하여 가챠 요청 ID의 응답, 보유 캐릭터, Redis 재화·전투력, PostgreSQL 저장값을 대조한다.
4. `rollback`으로 baseline을 복원하고 같은 상태를 대조한다.
5. 런타임 설정 JSON을 일부러 깨뜨린 패키지를 배포한다. 실제 .NET 프로세스 기동이 실패하고 baseline이 자동 복원되는지 확인한다. 이는 자연 발생 장애가 아닌 주입 실패다.
6. manifest 생성 이후 DLL을 변조한 패키지가 거절되는지, 이 거절이 정상 서비스의 PID를 바꾸지 않는지 확인한다. 외부 환경 파일도 전 과정에서 동일해야 한다.

`release.py stage`, `activate`, `rollback`은 독립 CLI다. 이미 이 구조로 준비된 격리 호스트에서는 다음처럼 사용한다.

```bash
python3 tools/linux/release.py stage /srv/trickcal-demo /tmp/published-release
python3 tools/linux/release.py activate /srv/trickcal-demo candidate-<sha> \
  --unit trickcal-deploy-demo.service --port 19100 --account deploy_probe_demo
python3 tools/linux/release.py rollback /srv/trickcal-demo \
  --unit trickcal-deploy-demo.service --port 19100 --account deploy_probe_demo
```

단독 CLI는 systemd unit이나 환경 파일을 자동으로 설치하지 않는다. `verify_deployment.py`가 생성하는 unit/환경 파일의 구조를 먼저 준비해야 한다. 로그인 probe는 테스트 계정을 만들거나 세션을 변경하므로 순수 읽기 전용 health check가 아니다.

## 트레이드오프와 남은 범위

- 단일 인스턴스를 재시작하므로 접속 중단과 재접속이 발생한다. 원자적인 링크 교체가 무중단을 뜻하지 않는다.
- 애플리케이션 파일만 되돌리며 PostgreSQL/Redis 데이터와 환경 파일은 되감지 않는다. 호환되지 않는 DB 스키마 변경은 별도 migration/복구 절차가 필요하다.
- 이번 baseline/candidate는 배포 도구 추가 전후의 커밋이다. 게임 로직은 같고 빌드에 포함한 커밋 식별자가 다르다. 실제 프로세스·배포물 교체는 검증하지만, 서로 다른 게임 기능이나 스키마 간 호환성까지 검증한 것은 아니다.
- SHA-256 manifest는 파일 훼손 감지다. 신뢰할 수 없는 게시자의 서명 검증이나 공급망 인증을 대신하지 않는다.
- 1대 호스트의 정상 동작·기동 실패 복구를 검증한다. 3만 동접, 다중 서버 배포, 호스트 장애, Redis 데이터 유실 복구, 운영 SLA는 이 검사의 범위가 아니다.
