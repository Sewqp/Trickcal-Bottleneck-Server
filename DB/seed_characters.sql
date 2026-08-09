-- 캐릭터 마스터 시드 데이터 (테스트용) — 실제 트릭컬 리바이브 콘텐츠와 무관한 가상 캐릭터 4종.
-- 대상 DB(trickcal_bottleneck)에 schema.sql 적용 후 실행할 것.
-- character_id는 IDENTITY라 직접 지정하지 않고, 보드 시드는 info->>'name'으로 조회해서 참조함
-- (재실행 시 중복 삽입되므로 여러 번 실행하지 않을 것 — 필요하면 먼저 TRUNCATE).

-- ──────────────────────────────────────────────────────────────
-- character_info — base_stat/stat_growth_table 구조는 아직 계산 로직이 없어서(파싱은
-- 나중에 필요해질 때) 임시로 정한 형태: growth는 "레벨업 1회당 증가량" 고정값
-- ──────────────────────────────────────────────────────────────
INSERT INTO character_info (info, base_stat, stat_growth_table) VALUES
    (
        '{"name": "루나", "desc": "밸런스형"}',
        '{"hp": 800, "main_atk": 120, "phys_def": 60, "magic_def": 60}',
        '{"hp": 8, "main_atk": 3, "phys_def": 2, "magic_def": 2}'
    ),
    (
        '{"name": "시안", "desc": "물리 딜러"}',
        '{"hp": 650, "main_atk": 160, "phys_def": 40, "magic_def": 30}',
        '{"hp": 6, "main_atk": 4, "phys_def": 1, "magic_def": 1}'
    ),
    (
        '{"name": "테오", "desc": "탱커"}',
        '{"hp": 1100, "main_atk": 70, "phys_def": 110, "magic_def": 90}',
        '{"hp": 12, "main_atk": 2, "phys_def": 3, "magic_def": 3}'
    ),
    (
        '{"name": "노아", "desc": "마법 딜러"}',
        '{"hp": 600, "main_atk": 140, "phys_def": 30, "magic_def": 70}',
        '{"hp": 6, "main_atk": 4, "phys_def": 1, "magic_def": 2}'
    );

-- ──────────────────────────────────────────────────────────────
-- character_board (개인 보드, 캐릭터당 3개) — stat_type: 0=HP 1=메인공격력 2=물리방어 3=마법방어
-- ──────────────────────────────────────────────────────────────
INSERT INTO character_board (character_id, board_no, cost, stat_type, stat_value)
SELECT character_id, board_no, cost, stat_type, stat_value FROM (VALUES
    ('루나', 1, 100, 0, 50), ('루나', 2, 150, 1, 15), ('루나', 3, 200, 2, 10),
    ('시안', 1, 100, 1, 20), ('시안', 2, 150, 1, 20), ('시안', 3, 200, 0, 40),
    ('테오', 1, 100, 2, 20), ('테오', 2, 150, 3, 20), ('테오', 3, 200, 0, 100),
    ('노아', 1, 100, 1, 18), ('노아', 2, 150, 3, 15), ('노아', 3, 200, 0, 40)
) AS v(name, board_no, cost, stat_type, stat_value)
JOIN character_info ci ON ci.info ->> 'name' = v.name;

-- ──────────────────────────────────────────────────────────────
-- global_board (전체 보드, 캐릭터당 2개 배치) — character_id는 효과 대상이 아니라 배치 위치일 뿐
-- ──────────────────────────────────────────────────────────────
INSERT INTO global_board (character_id, board_no, cost, stat_type, stat_value)
SELECT character_id, board_no, cost, stat_type, stat_value FROM (VALUES
    ('루나', 1, 300, 0, 30), ('루나', 2, 400, 3, 10),
    ('시안', 1, 300, 1, 10), ('시안', 2, 400, 2, 10),
    ('테오', 1, 300, 2, 15), ('테오', 2, 400, 0, 60),
    ('노아', 1, 300, 3, 12), ('노아', 2, 400, 1, 10)
) AS v(name, board_no, cost, stat_type, stat_value)
JOIN character_info ci ON ci.info ->> 'name' = v.name;
