-- Trickcal-Bottleneck-Server Schema
-- PostgreSQL / UTF8
-- 대상 DB에 접속한 뒤 실행할 것 (CREATE DATABASE 문 없음)
-- 설계 근거: ../docs/design-plan-draft.md (rev.3, 2026-08-07)

-- MySQL의 `ON UPDATE CURRENT_TIMESTAMP` 대응 — UPDATE 시 updated_at 자동 갱신 트리거
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- ──────────────────────────────────────────────────────────────
-- player  (Game-Server-CSharp와 동일 구조 — 별도 DB이므로 재선언)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS player (
    player_id  BIGINT GENERATED ALWAYS AS IDENTITY,
    pname      VARCHAR(30) NOT NULL,
    status     SMALLINT    NOT NULL DEFAULT 0, -- 0=정상 1=정지 2=영구밴
    created_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (player_id),
    CONSTRAINT uq_player_pname UNIQUE (pname)
);

DROP TRIGGER IF EXISTS trg_player_updated_at ON player;
CREATE TRIGGER trg_player_updated_at
    BEFORE UPDATE ON player
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ──────────────────────────────────────────────────────────────
-- item_definition / item_instance / inventory
-- (Game-Server-CSharp와 동일 구조 — 시드 데이터는 이번 게임 콘텐츠에 맞춰 추후 별도 작성)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS item_definition (
    item_def_id INT GENERATED ALWAYS AS IDENTITY,
    item_name   VARCHAR(50) NOT NULL,
    item_desc   TEXT,
    item_type   SMALLINT    NOT NULL, -- 0=장비 1=소모품
    created_at  TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (item_def_id)
);

CREATE TABLE IF NOT EXISTS item_instance (
    item_instance_id BIGINT GENERATED ALWAYS AS IDENTITY,
    item_def_id      INT       NOT NULL,
    item_status      SMALLINT  NOT NULL DEFAULT 0, -- 0=정상 1=밴 2=만료
    expired_at       TIMESTAMP DEFAULT NULL,        -- NULL=영구
    PRIMARY KEY (item_instance_id),
    CONSTRAINT fk_instance_def
        FOREIGN KEY (item_def_id) REFERENCES item_definition (item_def_id)
);

CREATE TABLE IF NOT EXISTS inventory (
    inventory_id     BIGINT GENERATED ALWAYS AS IDENTITY,
    player_id        BIGINT    NOT NULL,
    item_instance_id BIGINT    NOT NULL,
    item_count       INT       NOT NULL DEFAULT 1,
    acquired_at      TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (inventory_id),
    CONSTRAINT fk_inventory_player
        FOREIGN KEY (player_id)        REFERENCES player        (player_id),
    CONSTRAINT fk_inventory_instance
        FOREIGN KEY (item_instance_id) REFERENCES item_instance (item_instance_id)
);

CREATE INDEX IF NOT EXISTS idx_inventory_player ON inventory (player_id);

-- ──────────────────────────────────────────────────────────────
-- character_info (마스터 — 캐릭터 종류별 공용 정의, player_id 없음)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS character_info (
    character_id      INT GENERATED ALWAYS AS IDENTITY,
    info               JSONB NOT NULL, -- 이름/설명 등
    base_stat          JSONB NOT NULL, -- 기본 스탯(hp/main_atk/phys_def/magic_def)
    stat_growth_table  JSONB NOT NULL, -- 레벨→스탯 증가량 고정 테이블(결정론적)
    PRIMARY KEY (character_id)
);

-- ──────────────────────────────────────────────────────────────
-- character_status (플레이어별 캐릭터 보유 상태 — 계정 생성 시 전체 row 미리 생성)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS character_status (
    player_id    BIGINT    NOT NULL,
    character_id INT       NOT NULL,
    status       SMALLINT  NOT NULL DEFAULT 0, -- 0=미보유 1=보유
    level        INT       NOT NULL DEFAULT 1,
    acquired_at  TIMESTAMP DEFAULT NULL, -- NULL=아직 미보유, 획득 시점(status 0→1)에 채워짐
    PRIMARY KEY (player_id, character_id),
    CONSTRAINT fk_cs_player
        FOREIGN KEY (player_id)    REFERENCES player         (player_id)    ON DELETE CASCADE,
    CONSTRAINT fk_cs_character
        FOREIGN KEY (character_id) REFERENCES character_info (character_id)
);

-- ──────────────────────────────────────────────────────────────
-- player_stat (기존 character_stat 대체 — redis가 기준값, 여기는 flush 시점 스냅샷)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS player_stat (
    player_id          BIGINT NOT NULL,
    total_combat_power BIGINT NOT NULL DEFAULT 0, -- redis에서 반올림된 정수 기준으로 flush됨
    PRIMARY KEY (player_id),
    CONSTRAINT fk_ps_player
        FOREIGN KEY (player_id) REFERENCES player (player_id) ON DELETE CASCADE
);

-- ──────────────────────────────────────────────────────────────
-- player_currency (redis가 기준값, 여기는 flush 시점 스냅샷 — player_stat과 동일 패턴)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS player_currency (
    player_id BIGINT NOT NULL,
    gold      BIGINT NOT NULL DEFAULT 0, -- 무료 재화
    elleaf    BIGINT NOT NULL DEFAULT 0, -- 유료 재화(가챠 전용)
    macaron   BIGINT NOT NULL DEFAULT 0, -- 캐릭터 강화 전용 재화
    PRIMARY KEY (player_id),
    CONSTRAINT fk_pc_player
        FOREIGN KEY (player_id) REFERENCES player (player_id) ON DELETE CASCADE
);

-- ──────────────────────────────────────────────────────────────
-- character_board (마스터 — 캐릭터 전용 개인 보드, 효과는 자신에게만 적용)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS character_board (
    character_id INT      NOT NULL,
    board_no     INT      NOT NULL,
    cost         INT      NOT NULL,
    stat_type    SMALLINT NOT NULL, -- 0=HP 1=메인 공격력 2=물리 방어력 3=마법 방어력
    stat_value   INT      NOT NULL,
    PRIMARY KEY (character_id, board_no),
    CONSTRAINT fk_cb_character
        FOREIGN KEY (character_id) REFERENCES character_info (character_id),
    CONSTRAINT chk_cb_stat_type CHECK (stat_type BETWEEN 0 AND 3)
);

-- ──────────────────────────────────────────────────────────────
-- player_character_board (캐릭터 획득 시 그 캐릭터의 보드 전부를 bulk insert로 미리 생성)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS player_character_board (
    player_id    BIGINT   NOT NULL,
    character_id INT      NOT NULL,
    board_no     INT      NOT NULL,
    status       SMALLINT NOT NULL DEFAULT 0, -- 0=미해금 1=해금
    PRIMARY KEY (player_id, character_id, board_no),
    CONSTRAINT fk_pcb_player
        FOREIGN KEY (player_id)                REFERENCES player (player_id) ON DELETE CASCADE,
    CONSTRAINT fk_pcb_board
        FOREIGN KEY (character_id, board_no)   REFERENCES character_board (character_id, board_no)
);

-- ──────────────────────────────────────────────────────────────
-- global_board (마스터 — 전체 적용 보드, character_id는 배치 위치일 뿐 효과 범위 아님)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS global_board (
    character_id INT      NOT NULL, -- 효과 대상 아님 — 어느 캐릭터 보드 시퀀스에 배치되는지(클라 표시 위치)
    board_no     INT      NOT NULL,
    cost         INT      NOT NULL,
    stat_type    SMALLINT NOT NULL, -- 0=HP 1=메인 공격력 2=물리 방어력 3=마법 방어력
    stat_value   INT      NOT NULL,
    PRIMARY KEY (character_id, board_no),
    CONSTRAINT fk_gb_character
        FOREIGN KEY (character_id) REFERENCES character_info (character_id),
    CONSTRAINT chk_gb_stat_type CHECK (stat_type BETWEEN 0 AND 3)
);

-- ──────────────────────────────────────────────────────────────
-- player_global_board (character_board와 동일 트리거·동일 bulk insert로 캐릭터 획득 시 미리 생성)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS player_global_board (
    player_id    BIGINT   NOT NULL,
    character_id INT      NOT NULL,
    board_no     INT      NOT NULL,
    status       SMALLINT NOT NULL DEFAULT 0, -- 0=미해금 1=해금
    PRIMARY KEY (player_id, character_id, board_no),
    CONSTRAINT fk_pgb_player
        FOREIGN KEY (player_id)              REFERENCES player (player_id) ON DELETE CASCADE,
    CONSTRAINT fk_pgb_board
        FOREIGN KEY (character_id, board_no) REFERENCES global_board (character_id, board_no)
);

-- ──────────────────────────────────────────────────────────────
-- guild / guild_member (Game-Server-CSharp와 동일 구조 — channel은 채팅 전용이라 제외)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS guild (
    guild_id     BIGINT GENERATED ALWAYS AS IDENTITY,
    guild_name   VARCHAR(30) NOT NULL,
    guild_status SMALLINT    NOT NULL DEFAULT 0, -- 0=정상 1=해산
    created_at   TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (guild_id),
    CONSTRAINT uq_guild_name UNIQUE (guild_name)
);

CREATE TABLE IF NOT EXISTS guild_member (
    guild_id  BIGINT    NOT NULL,
    player_id BIGINT    NOT NULL,
    role      SMALLINT  NOT NULL DEFAULT 0, -- 0=일반 1=부길드장 2=길드장
    joined_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (guild_id, player_id),
    CONSTRAINT fk_gm_guild
        FOREIGN KEY (guild_id)  REFERENCES guild  (guild_id) ON DELETE CASCADE,
    CONSTRAINT fk_gm_player
        FOREIGN KEY (player_id) REFERENCES player (player_id) ON DELETE CASCADE
);

-- ──────────────────────────────────────────────────────────────
-- match_history / match_player (Game-Server-CSharp와 동일 구조)
-- ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS match_history (
    match_id     BIGINT GENERATED ALWAYS AS IDENTITY,
    match_type   SMALLINT  NOT NULL,
    player_count INT       NOT NULL,
    started_at   TIMESTAMP NOT NULL,
    ended_at     TIMESTAMP DEFAULT NULL,
    PRIMARY KEY (match_id)
);

CREATE TABLE IF NOT EXISTS match_player (
    match_id  BIGINT   NOT NULL,
    player_id BIGINT   NOT NULL,
    result    SMALLINT NOT NULL DEFAULT 0, -- 0=패 1=승
    PRIMARY KEY (match_id, player_id),
    CONSTRAINT fk_mp_match
        FOREIGN KEY (match_id)  REFERENCES match_history (match_id) ON DELETE CASCADE,
    CONSTRAINT fk_mp_player
        FOREIGN KEY (player_id) REFERENCES player        (player_id) ON DELETE CASCADE
);
