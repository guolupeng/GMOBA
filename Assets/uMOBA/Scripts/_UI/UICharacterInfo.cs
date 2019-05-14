using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class UICharacterInfo : MonoBehaviour {
    public Text damageText;
    public Text defenseText;
    public Text speedText;

    void Update() {
        var player = Utils.ClientLocalPlayer();
        if (!player) return;

        // show all stats like base(+bonus)
        damageText.text = player.baseDamage + " ( + " + (player.damage-player.baseDamage) + ")";
        defenseText.text = player.baseDefense + " ( + " + (player.defense-player.baseDefense) + ")";
        speedText.text = player.speed + " ( + 0)";
    }
}
