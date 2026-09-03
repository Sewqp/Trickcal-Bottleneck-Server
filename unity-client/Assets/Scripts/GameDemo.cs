using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// "UI 이탈 시에만 서버에 저장된다"는 이 서버 설계의 핵심을 영상으로 보여주기 위한 최소 데모.
// 화면은 로그인 -> 캐릭터 목록(없으면 가챠) -> 강화(로컬 +1 누적, "나가기"에서만 서버로 커밋) 3단계뿐.
// 예쁜 UI가 목적이 아니라 "나가기가 눌리는 순간 실제로 반영됨"이 카메라에 또렷이 보이는 게 목적이라
// OnGUI(씬/프리팹 없이 컴포넌트 하나로 동작)로 작성함 — GameServer/GameClient(콘솔)와 같은 와이어
// 프로토콜을 그대로 재구현해서 쓴다(4바이트 헤더, 리틀엔디안).
public sealed class GameDemo : MonoBehaviour
{
    private enum Screen { Login, CharacterList, Enhance, Disconnected }

    private NetworkClient _net;
    private Screen _screen = Screen.Login;
    private string _status = "";
    private string _pending; // null이면 대기 중인 요청 없음 — 버튼 입력 게이트

    // 로그인
    private string _host = "127.0.0.1";
    private string _portText = "9100";
    private string _playerName = "admin"; // 데모 녹화용 기본값 — admin으로 로그인하면 디버그 치트가 안전하게 동작
    private long _playerId;
    private float _heartbeatTimer;

    // 캐릭터 목록
    private readonly List<(int CharacterId, byte Status, int Level)> _characters = new();

    // 강화 화면
    private int _enhanceCharacterId;
    private int _committedLevel;   // 서버가 마지막으로 확정한 레벨
    private int _localLevel;       // 화면 안에서만 누적된 목표 레벨(아직 미전송)
    private long _macaron, _gold;

    private GUIStyle _titleStyle, _labelStyle, _buttonStyle, _boxStyle;

    private void Awake()
    {
        _net = new NetworkClient();
    }

    private void OnDestroy() => _net?.Close();

    private void Update()
    {
        while (_net.Incoming.TryDequeue(out var packet))
            HandlePacket((PacketId)packet.Id, packet.Payload);

        if (_screen != Screen.Login && _screen != Screen.Disconnected && !_net.IsConnected)
        {
            _status = "연결이 끊겼습니다" + (string.IsNullOrEmpty(_net.LastError) ? "." : $" ({_net.LastError})");
            _screen = Screen.Disconnected;
        }

        if (_net.IsConnected && _playerId != 0)
        {
            _heartbeatTimer += Time.deltaTime;
            if (_heartbeatTimer >= 10f)
            {
                _heartbeatTimer = 0f;
                _net.Send((ushort)PacketId.Heartbeat);
            }
        }
    }

    // ── 스타일 (Editor 실행 중에만 GUI 컨텍스트가 유효하므로 OnGUI 첫 프레임에 lazy 초기화) ──
    private void EnsureStyles()
    {
        if (_titleStyle != null) return;
        _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
        _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
        _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, padding = new RectOffset(12, 12, 10, 10) };
        _boxStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.UpperLeft, wordWrap = true };
    }

    private void OnGUI()
    {
        EnsureStyles();
        GUILayout.BeginArea(new Rect(30, 30, 560, 700));

        switch (_screen)
        {
            case Screen.Login: DrawLogin(); break;
            case Screen.CharacterList: DrawCharacterList(); break;
            case Screen.Enhance: DrawEnhance(); break;
            case Screen.Disconnected: DrawDisconnected(); break;
        }

        GUILayout.EndArea();
    }

    // ── 로그인 화면 ──
    private void DrawLogin()
    {
        GUILayout.Label("Trickcal-Bottleneck-Server 데모 클라", _titleStyle);
        GUILayout.Space(20);

        GUILayout.Label("host", _labelStyle);
        _host = GUILayout.TextField(_host, _labelStyle);
        GUILayout.Label("port", _labelStyle);
        _portText = GUILayout.TextField(_portText, _labelStyle);
        GUILayout.Label("플레이어 이름 (admin으로 로그인하면 디버그 치트가 정상 동작)", _labelStyle);
        _playerName = GUILayout.TextField(_playerName, _labelStyle);

        GUILayout.Space(20);
        GUI.enabled = _pending == null;
        if (GUILayout.Button("접속 + 로그인", _buttonStyle))
            ConnectAndLogin();
        GUI.enabled = true;

        GUILayout.Space(20);
        GUILayout.Label(_status, _labelStyle);
    }

    private void ConnectAndLogin()
    {
        if (!int.TryParse(_portText, out int port)) { _status = "포트가 숫자가 아닙니다."; return; }
        _status = "접속 중...";
        if (!_net.Connect(_host, port)) { _status = $"접속 실패: {_net.LastError}"; return; }

        var nameBytes = Encoding.UTF8.GetBytes(_playerName);
        var payload = new byte[2 + nameBytes.Length];
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(payload, 0);
        nameBytes.CopyTo(payload, 2);
        _net.Send((ushort)PacketId.LoginRequest, payload);
        _pending = "login";
        _status = "로그인 요청 보냄...";
    }

    // ── 캐릭터 목록 화면 ──
    private void DrawCharacterList()
    {
        GUILayout.Label($"{_playerName} (playerId={_playerId})", _titleStyle);
        GUILayout.Space(10);

        GUI.enabled = _pending == null;
        if (GUILayout.Button("[테스트] 골드/마카롱 획득", _buttonStyle))
            RequestDebugGrantCurrency();
        GUILayout.Space(10);

        foreach (var (characterId, status, level) in _characters)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"#{characterId}  {(status == 1 ? "보유" : "미보유")}  Lv.{level}", _labelStyle, GUILayout.Width(320));
            if (status == 1 && GUILayout.Button("강화하러 가기", _buttonStyle))
                RequestEnhanceEnter(characterId, level);
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(10);
        if (GUILayout.Button("가챠 (10연챠, 1000 엘리프)", _buttonStyle))
            RequestGacha();
        if (GUILayout.Button("목록 새로고침", _buttonStyle))
            RequestCharacterList();
        GUI.enabled = true;

        GUILayout.Space(20);
        GUILayout.Label(_status, _boxStyle, GUILayout.Height(80));
    }

    private void RequestCharacterList()
    {
        _net.Send((ushort)PacketId.CharacterListRequest);
        _pending = "characterList";
        _status = "캐릭터 목록 요청...";
    }

    private void RequestGacha()
    {
        _net.Send((ushort)PacketId.GachaRequest);
        _pending = "gacha";
        _status = "가챠 요청...";
    }

    private void RequestDebugGrantCurrency()
    {
        _net.Send((ushort)PacketId.DebugGrantCurrencyRequest);
        _pending = "debugGrant";
        _status = "[테스트] 재화 지급 요청...";
    }

    private void RequestEnhanceEnter(int characterId, int currentLevel)
    {
        _enhanceCharacterId = characterId;
        _committedLevel = currentLevel;
        var payload = new byte[4];
        BitConverter.GetBytes(characterId).CopyTo(payload, 0);
        _net.Send((ushort)PacketId.CharacterEnhanceEnterRequest, payload);
        _pending = "enhanceEnter";
        _status = "강화 UI 진입 요청...";
    }

    // ── 강화 화면 (이 데모의 핵심) ──
    private void DrawEnhance()
    {
        GUILayout.Label($"강화 UI — 캐릭터 #{_enhanceCharacterId}", _titleStyle);
        GUILayout.Space(10);
        GUILayout.Label($"서버에 저장된 레벨(커밋됨): {_committedLevel}", _labelStyle);
        GUILayout.Label($"보유 마카롱: {_macaron}   보유 골드: {_gold}", _labelStyle);
        GUILayout.Space(10);

        var pendingStyle = new GUIStyle(_titleStyle) { normal = { textColor = _localLevel > _committedLevel ? Color.yellow : Color.white } };
        GUILayout.Label($"로컬 목표 레벨: {_localLevel}" + (_localLevel > _committedLevel ? "  (아직 서버 미전송!)" : ""), pendingStyle);

        GUILayout.Space(20);
        GUI.enabled = _pending == null;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("+1 레벨업 (로컬에만 반영)", _buttonStyle))
            _localLevel++;
        if (GUILayout.Button("취소 (변경 버림)", _buttonStyle))
            _localLevel = _committedLevel;
        GUILayout.EndHorizontal();

        GUILayout.Space(10);
        if (GUILayout.Button("나가기 — 지금 서버에 저장", _buttonStyle))
            RequestEnhanceExit();
        if (GUILayout.Button("캐릭터 목록으로", _buttonStyle))
            RequestCharacterList();
        GUI.enabled = true;

        GUILayout.Space(20);
        GUILayout.Label(_status, _boxStyle, GUILayout.Height(100));
    }

    private void RequestEnhanceExit()
    {
        var payload = new byte[8];
        BitConverter.GetBytes(_enhanceCharacterId).CopyTo(payload, 0);
        BitConverter.GetBytes(_localLevel).CopyTo(payload, 4);
        _net.Send((ushort)PacketId.CharacterEnhanceExitRequest, payload);
        _pending = "enhanceExit";
        _status = _localLevel > _committedLevel
            ? $"[나가기] 로컬에서 누적한 목표 레벨 {_localLevel}을 서버로 전송합니다..."
            : "[나가기] 변경 없음 — 그래도 이탈 요청을 보냅니다(락 해제)...";
    }

    // ── 연결 끊김 화면 ──
    private void DrawDisconnected()
    {
        GUILayout.Label("연결 끊김", _titleStyle);
        GUILayout.Label(_status, _boxStyle, GUILayout.Height(80));
        GUILayout.Space(10);
        if (GUILayout.Button("로그인 화면으로", _buttonStyle))
        {
            _net.Close();
            _net = new NetworkClient();
            _playerId = 0;
            _pending = null;
            _screen = Screen.Login;
        }
    }

    // ── 서버 응답 처리 ──
    private void HandlePacket(PacketId id, byte[] p)
    {
        if (id == PacketId.Heartbeat) return; // 백그라운드 생존 신호 — 화면 상태와 무관

        switch (id)
        {
            case PacketId.LoginResult when _pending == "login":
                _pending = null;
                if (p.Length < 9 || p[0] != 1) { _status = "로그인 실패"; return; }
                _playerId = BitConverter.ToInt64(p, 1);
                _status = $"로그인 성공. playerId={_playerId}";
                _screen = Screen.CharacterList;
                RequestCharacterList();
                break;

            case PacketId.CharacterListResult when _pending == "characterList":
                _pending = null;
                _characters.Clear();
                if (p.Length >= 2)
                {
                    ushort count = BitConverter.ToUInt16(p, 0);
                    int offset = 2;
                    for (int i = 0; i < count; i++)
                    {
                        int characterId = BitConverter.ToInt32(p, offset); offset += 4;
                        byte status = p[offset]; offset += 1;
                        int level = BitConverter.ToInt32(p, offset); offset += 4;
                        _characters.Add((characterId, status, level));
                    }
                }
                _status = $"캐릭터 {_characters.Count}종 로드됨.";
                break;

            case PacketId.GachaResult when _pending == "gacha":
                _pending = null;
                _status = (p.Length >= 1 && p[0] == 1) ? "가챠 성공! 목록을 새로고침합니다." : "가챠 실패(엘리프 부족).";
                RequestCharacterList();
                break;

            case PacketId.DebugGrantCurrencyResult when _pending == "debugGrant":
                _pending = null;
                if (p.Length >= 17 && p[0] == 1)
                {
                    long grantedGold = BitConverter.ToInt64(p, 1);
                    long grantedMacaron = BitConverter.ToInt64(p, 9);
                    _status = $"[테스트] 재화 지급됨. 골드={grantedGold}, 마카롱={grantedMacaron}\n(admin이 아니면 곧 연결이 끊깁니다)";
                }
                else
                {
                    _status = "[테스트] 재화 지급 실패";
                }
                break;

            case PacketId.CharacterEnhanceEnterResult when _pending == "enhanceEnter":
                _pending = null;
                if (p.Length < 24) { _status = "강화 진입 실패"; return; }
                _committedLevel = BitConverter.ToInt32(p, 4);
                _localLevel = _committedLevel;
                _macaron = BitConverter.ToInt64(p, 8);
                _gold = BitConverter.ToInt64(p, 16);
                _status = "강화 UI 진입 완료. '+1'은 로컬에만 반영되고, '나가기'를 눌러야 서버에 저장됩니다.";
                _screen = Screen.Enhance;
                break;

            case PacketId.CharacterEnhanceExitResult when _pending == "enhanceExit":
                _pending = null;
                if (p.Length < 37) { _status = "강화 이탈 응답 이상"; return; }
                bool success = p[0] == 1;
                int finalLevel = BitConverter.ToInt32(p, 1);
                long macaron = BitConverter.ToInt64(p, 5);
                long gold = BitConverter.ToInt64(p, 13);
                long macaronSpent = BitConverter.ToInt64(p, 21);
                long goldSpent = BitConverter.ToInt64(p, 29);

                _committedLevel = finalLevel;
                _localLevel = finalLevel;
                _macaron = macaron;
                _gold = gold;

                _status = success
                    ? $"[서버 저장 완료] 레벨 {finalLevel} 커밋됨. 소모 마카롱 {macaronSpent}, 소모 골드 {goldSpent}."
                    : $"[강화 실패] 마카롱 또는 골드 부족. 레벨 {finalLevel} 유지.";
                break;
        }
    }
}
