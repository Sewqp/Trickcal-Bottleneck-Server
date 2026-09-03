using UnityEngine;

// 씬에 직접 GameObject를 배치하지 않고, 어떤 씬이 로드되든 실행 시작 시 자동으로 GameDemo를 붙인
// GameObject 하나를 만든다 — 씬 파일을 손으로/툴 없이 구성하는 위험을 피하기 위한 선택.
public static class Bootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        var go = new GameObject("GameDemo");
        go.AddComponent<GameDemo>();
        Object.DontDestroyOnLoad(go);
    }
}
