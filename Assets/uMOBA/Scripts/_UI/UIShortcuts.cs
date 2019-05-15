using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class UIShortcuts : MonoBehaviour {
    public Button quitButton;

    void Update() {
        var player = Utils.ClientLocalPlayer();
        if (!player) return;

        quitButton.onClick.SetListener(() => {
            // stop editor or application
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            NetworkManager.singleton.StopClient();
            Application.Quit();
#endif
        });
    }
}
