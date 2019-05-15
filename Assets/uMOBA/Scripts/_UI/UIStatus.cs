using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class UIStatus : MonoBehaviour {
    public GameObject panel;

    void Update() {
        // local player joined the world? then done loading
        if (Utils.ClientLocalPlayer() != null)
            panel.SetActive(false);
    }

    public void Show(string message) {
        panel.SetActive(true);
        panel.GetComponentInChildren<Text>().text = message;
    }
}