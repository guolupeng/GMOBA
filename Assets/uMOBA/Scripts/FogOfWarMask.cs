﻿using UnityEngine;

public class FogOfWarMask : MonoBehaviour {
    public Entity owner;

	void Update() {
        var player = Utils.ClientLocalPlayer();
        if (!player) return;

        // show the mask if same team as local player, otherwise hide it
        GetComponent<MeshRenderer>().enabled = player.team == owner.team;
	}
}
