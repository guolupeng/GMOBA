using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class UIExperienceBar : MonoBehaviour {
    public Slider slider;
    public Text statusText;

    void Update() {
        var player = Utils.ClientLocalPlayer();
        if (!player) return;

        slider.value = player.ExperiencePercent();
        statusText.text = "Lv." + player.level;
    }
}