using UnityEditor;
using UnityEditor.SceneManagement;

// 배치 모드(-executeMethod)로 한 번 실행해서 빈 씬 하나를 만들고 Build Settings에 등록하는 용도.
// GameDemo는 Bootstrap.cs의 RuntimeInitializeOnLoadMethod가 씬 내용과 무관하게 알아서 붙이므로,
// 이 씬 자체는 완전히 비어 있어도 된다(손으로 .unity YAML을 작성하는 위험을 피함).
public static class SceneBootstrapper
{
    public static void CreateDemoScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const string path = "Assets/Scenes/Demo.unity";
        System.IO.Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, path);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
    }
}
