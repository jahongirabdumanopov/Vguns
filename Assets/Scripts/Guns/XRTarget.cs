using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VGuns.Broadcast;

public class XRTarget : MonoBehaviour {
    
    public float health;
    
    public TextMeshPro livesText;
    
    public GameObject deathScreen;

    public GameObject LocomotionSystem;

    public GameObject ray_Right;
    public GameObject ray_Left;
        
    public GameObject TeleportManagerLeft;
    public GameObject TeleportManagerRight;

    public GameObject[] enemies;


    // How long the AI director's death camera plays before returning to the menu
    // (the old code meant to wait 5 s but loaded the menu immediately).
    public float deathCamSeconds = 5f;

    bool isDead;

    void Update()
    {
        livesText.text = "Health: " + health;

        if (health <= 0 && !isDead)
        {
            isDead = true;
            Die();
        }
    }

    void Die()
    {
        GameEventBus.Raise(GameEventType.PlayerDeath, Camera.main != null ? Camera.main.transform.position : transform.position,
            "enemy", "player", 0f, "health reached 0"); // telemetry / AI director

        enemies = GameObject.FindGameObjectsWithTag("Enemy");
        foreach (GameObject enemy in enemies)
        {
            enemy.SetActive(false);
        }

        ray_Right.SetActive(true);
        ray_Left.SetActive(true);

        LocomotionSystem.SetActive(false);

        TeleportManagerLeft.SetActive(false);
        TeleportManagerRight.SetActive(false);

        deathScreen.SetActive(true);

        StartCoroutine(ReturnToMenu());
    }

    IEnumerator ReturnToMenu()
    {
        // Realtime so slow-motion / pause can't stretch it.
        yield return new WaitForSecondsRealtime(deathCamSeconds);
        SceneManager.LoadScene(0);
    }


    /// 'Hits' the target for a certain amount of damage
    public void Hit(float damage) {
        if (isDead) return;
        health -= damage;
        GameEventBus.Raise(GameEventType.PlayerHit, transform.position, "enemy", "player", damage, "health " + health); // telemetry / AI director
    }
    
}