-- Trickcal-Bottleneck-Server — 초기 row 생성용 bulk insert 패턴
-- 설계 근거: ../docs/design-plan-draft.md 1-4절 (row별 loop INSERT 금지, 단일 INSERT ... SELECT로 처리)
-- $1, $2 는 Npgsql 파라미터 자리표시자 — 실제로는 각 Repository 메서드에서 이 SQL을 그대로 실행

-- ──────────────────────────────────────────────────────────────
-- 1. 계정 생성 시 — character_status 전체 캐릭터분 미리 생성
--    트리거: player row INSERT 직후 (같은 트랜잭션)
-- ──────────────────────────────────────────────────────────────
INSERT INTO character_status (player_id, character_id, status, level)
SELECT $1, character_id, 0, 1
FROM character_info;

-- ──────────────────────────────────────────────────────────────
-- 2. 캐릭터 획득 시 — 그 캐릭터의 개인 보드(character_board) 전부 미리 생성
--    트리거: character_status.status를 0→1로 갱신하는 시점 (같은 트랜잭션)
-- ──────────────────────────────────────────────────────────────
INSERT INTO player_character_board (player_id, character_id, board_no, status)
SELECT $1, $2, board_no, 0
FROM character_board
WHERE character_id = $2;

-- ──────────────────────────────────────────────────────────────
-- 3. 캐릭터 획득 시 — 그 캐릭터에 배치된 전체 보드(global_board) 전부 미리 생성
--    트리거: 2번과 동일 시점(같은 트랜잭션) — character_board와 동일 트리거·동일 bulk insert
--    주의: global_board.character_id는 효과 대상이 아니라 배치 위치일 뿐이지만,
--          "이 캐릭터를 뽑았을 때 그 캐릭터 시퀀스에 배치된 보드"를 미리 까는 기준으로는
--          character_id로 필터링하는 게 맞음 (효과는 나중에 계산 시 캐릭터 무관하게 전부 합산)
-- ──────────────────────────────────────────────────────────────
INSERT INTO player_global_board (player_id, character_id, board_no, status)
SELECT $1, $2, board_no, 0
FROM global_board
WHERE character_id = $2;
