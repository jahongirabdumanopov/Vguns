using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using VGuns.Broadcast;

public class BulletTime : MonoBehaviour
{
    [SerializeField] public InputActionReference SlowMoActivate;

    public TimeManager TimeManager;

    // Start is called before the first frame update
    void Start()
    {
        SlowMoActivate.action.performed += BulletTimeActivate;
    }

    private void BulletTimeActivate(InputAction.CallbackContext obj)
    {
        TimeManager.DoSlowmotion();
        GameEventBus.Raise(GameEventType.BulletTimeStarted, transform.position, "player", "", TimeManager.slowdownFactor); // telemetry / AI director
        
        Debug.Log("fuck you");
    }
}
