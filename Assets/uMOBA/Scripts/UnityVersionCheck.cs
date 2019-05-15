using UnityEngine;

public class UnityVersionCheck : MonoBehaviour {
    string recommended = "2017.4.7f1";

    void Awake() {
        if (Application.unityVersion != recommended) {
            string download = recommended.Contains("p")
                              ? "https://unity3d.com/unity/qa/patch-releases/" + recommended
                              : "https://unity3d.com/get-unity/download/archive";
            Debug.LogWarning("uMOBA works best with Unity " + recommended + "! Download: " + download + "\n");
        }
    }
}
