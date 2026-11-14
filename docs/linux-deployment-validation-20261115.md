# Linux 배포·롤백 실행 기록 — 2026-11-15

## 검증한 이유

서버가 테스트를 통과하더라도 실제 배포에서 실행 경로나 환경 설정이 달라지면 기동에 실패할 수 있다. 새 버전의 실행 여부를 확인하고, 실패했을 때 기존 플레이어 상태를 유지한 채 이전 실행물로 돌아갈 수 있는지 확인했다.

GitHub Actions의 Ubuntu 호스트에 실제 GameServer를 systemd 서비스로 설치했다. 실행물마다 별도 디렉터리를 사용하고, 환경 파일·로그·PostgreSQL·Redis는 릴리스 교체와 분리했다. 사람이 실행할 수 있는 배포 CLI와 같은 CLI를 호출하는 자동 검증기를 함께 두었다.

## 실행 환경과 재현 근거

| 항목 | 실제 실행값 |
|---|---|
| 배포 검증 | [Actions 43637188455 — 성공](https://github.com/Sewqp/Trickcal-Bottleneck-Server/actions/runs/43637188455) |
| 실행 시각 | 2026-11-15 05:22:55~05:24:06 KST, 배포 시나리오는 05:23:47~05:23:58 |
| 호스트 | GitHub hosted runner, Ubuntu 24.04.5 / systemd 255 |
| 실행물 | .NET 10, `linux-x64`, self-contained publish |
| 이전 릴리스 A | `b802c59673ba267ed16b8d27b00bb7ad979c5c2d` |
| 후보 릴리스 B | `5374916dce8e4f1bcda1b3277f4e5a878ac17f21` |
| APM 코드 참조 | `027d8d87cd9eec983521cbf767befb8c5ffc8075`, 실행 시 `APM_ENABLED=0` |
| 저장소 | 새 PostgreSQL 16 컨테이너의 `trickcal_checks_deploy`, 새 Redis 7 컨테이너의 DB 2 |
| 기존 정합성 검사 | [동일 후보 커밋의 CI 43637188372 — 성공](https://github.com/Sewqp/Trickcal-Bottleneck-Server/actions/runs/43637188372) |

기존 정합성 CI는 40,298 assertions와 GameServer 강제 종료·재시작 복구를 실행한다. 이번 배포 시나리오는 별도의 잡에서 실행했으며, 부하 테스트가 아니다. 원래 사용하던 로컬 DB와 사용자 작업 파일은 검증에 사용하지 않았다.

두 실행물은 각각 396개 파일로 패키징했다. 게임 로직은 같고 빌드에 포함된 커밋 식별자가 다르다. `GameServer.dll` SHA-256도 서로 다르며, 전체 값은 [실행 집계 JSON](evidence/linux-deployment-20261115.json)에 보관했다. 이 검증으로 서로 다른 게임 기능이나 DB 스키마 간 호환성까지 주장하지 않는다.

## 실제 배포·복원 결과

성공 판정은 프로세스 존재나 포트 개방만으로 하지 않았다. systemd MainPID의 `/proc/<pid>/exe`가 목표 릴리스 경로인지 검사한 뒤, TCP 로그인과 하트비트 3회 응답을 확인했다.

| 단계 | 확인한 결과 | 명령 내부 경과 시간 |
|---|---|---:|
| A 최초 배포 | A 경로의 프로세스 PID 3831, 로그인·하트비트 성공 | 1.934초 |
| A → B 배포 | B 경로의 PID 3912, 기존 TCP 연결 종료 후 재접속 성공 | 1.838초 |
| B → A 수동 롤백 | A 경로의 PID 4000, 로그인·하트비트 성공 | 2.075초 |
| 기동 실패 → A 자동 복원 | 잘못된 런타임 JSON으로 실제 프로세스 기동 실패, A PID 4115로 복원 | 2.043초 |
| 파일 훼손 거부 | manifest 작성 후 DLL을 변경한 패키지 거부, PID 4115 유지 | 시간 측정 대상 아님 |

시간은 릴리스 활성화 명령 내부에서 링크 교체를 시작한 뒤 readiness가 통과할 때까지다. 자동 복원 시간에는 실패한 버전의 기동 시도와 기존 버전 재시작이 포함된다. 사전 파일 검증·복사 시간은 포함하지 않는다. 단일 실행의 관측값이며 사용자 전체의 장애 시간, 운영 RTO 또는 SLA 보장값이 아니다.

자동 복원 시 배포 명령은 정상 배포 성공으로 처리하지 않고 종료 코드 1을 반환했다. 검증기는 이 예상 실패와 복원 결과를 확인했다. DLL 훼손은 종료 코드 2와 `Release checksum/file inventory mismatch`로 거부됐다.

## 플레이어 상태 대조

A에서 테스트 플레이어(ID 2)가 가챠를 한 번 수행한 뒤, B 배포·수동 롤백·자동 복원 각각의 직후 같은 플레이어로 접속했다. 같은 가챠 요청 ID를 다시 보냈을 때 응답 바이트가 동일했고, 재화가 추가로 차감되지 않았다.

| 대조 항목 | B 배포 후 | 수동 롤백 후 | 자동 복원 후 |
|---|---|---|---|
| Redis 재화: gold / elleaf / macaron | 0 / 600 / 0 | 0 / 600 / 0 | 0 / 600 / 0 |
| Redis 전투력 | 1785 | 1785 | 1785 |
| PostgreSQL 재화·전투력 | 위 값과 일치 | 위 값과 일치 | 위 값과 일치 |
| 보유 캐릭터 패킷 | 최초 응답과 동일 | 최초 응답과 동일 | 최초 응답과 동일 |
| PostgreSQL 캐릭터 행 | ID 1~4, status 1, level 1 | 동일 | 동일 |
| 가챠 응답 SHA-256 | `2b7b6cde…cb892f73` | 동일 | 동일 |

가챠 결과와 전투력은 해당 실행의 값이다. 다시 실행할 때 난수에 따라 달라질 수 있으며, 검증기는 고정된 수치를 기대하지 않고 그 실행에서 얻은 최초 상태와 비교한다. PostgreSQL은 재시작 후 값을 확인했으므로, 종료 시 flush만을 다른 저장 경로와 분리해 증명한 결과는 아니다.

외부 `server.env`의 SHA-256은 전 과정에서 동일했다. 마지막 검증 시 A가 실행 중임을 확인한 뒤 테스트 서비스를 중지했다. 최종 systemd 상태는 `MainPID=0`, `inactive/dead`이며 자신이 설치한 고유 unit을 제거했다.

## 로그에서 함께 확인한 사항

- 정상 릴리스 종료마다 `Application is shutting down`, `Orleans Silo stopped`, systemd `Deactivated successfully`가 기록됐다.
- B 종료 중 Orleans 관리 통계 조회에서 `No active nodes are compatible with grain management`와 `SiloUnavailableException`이 기록됐다. 종료 시각의 관리 호출과 겹친 로그로 보이며, 이후 서비스 종료와 A 복원, 플레이어 상태 검사는 통과했다. 이를 오류 로그가 전혀 없는 종료로 표현하지 않는다. 종료 시 관리 통계 수집 순서는 후속 확인 대상으로 남긴다.
- 런타임 JSON 파싱 오류는 자동 복원을 확인하려고 의도적으로 주입했다. 기존 게임 로직에서 자연 발생한 장애로 분류하지 않는다.

## 선택한 방식과 한계

- **단순한 단일 서버 배포를 선택했다.** 릴리스 폴더와 `current`/`previous` 링크를 쓰면 복구할 파일을 명확히 정할 수 있다. 대신 프로세스를 재시작하므로 기존 연결이 끊긴다. 무중단 배포나 자동 클라이언트 재접속은 검증하지 않았다.
- **애플리케이션과 데이터를 분리했다.** 롤백해도 PostgreSQL/Redis를 과거 시점으로 되돌리지 않는다. 실행물 롤백만으로 스키마 변경이나 데이터 손상을 복구할 수 없다.
- **복원 성공에도 배포 실패를 알리도록 했다.** 배포 작업이 실패했음을 숨기지 않으면서 이전 정상 서비스의 복원 여부를 별도로 기록한다.
- **파일 해시로 훼손을 먼저 잡았다.** manifest 검증 실패는 서비스 재시작 전에 거부한다. 해시는 게시자 서명이나 공급망 인증을 대신하지 않는다.
- **격리된 실제 Linux 실행까지 확인했다.** 상용 운영 호스트, 다중 노드, 실제 트래픽, 호스트 장애, Redis 유실, 서로 다른 게임 기능·DB 스키마의 롤백은 이 결과에 포함하지 않는다.

## 남긴 파일

- [배포·롤백 절차](linux-deployment.md): 디렉터리 구조, 명령, 사전 조건, 실패 처리
- [워크플로](../.github/workflows/linux-deploy-checks.yml): 두 릴리스 publish 및 실제 systemd 검증
- [배포 도구](../tools/linux/release.py), [검증기](../tools/linux/verify_deployment.py), [TCP probe](../tools/linux/probe.py)
- [공개 실행 집계](evidence/linux-deployment-20261115.json): 원 실행의 summary, 서비스 종료 상태, 로그별 SHA-256
- Actions artifact `linux-deployment-evidence`: publish 로그 2개, 실행 로그, journal, unit 정의, 최종 서비스 상태, summary, 배포 event 총 8개 파일. 보관 기간은 14일이며 로컬에도 내려받아 보존했다. 환경 파일과 DB 암호는 artifact에 넣지 않았다.

재현은 [배포·롤백 절차](linux-deployment.md)의 새 DB·Redis 조건과 워크플로를 따른다. 원본 DB를 비워 테스트 조건에 맞추는 방식은 사용하지 않는다.
