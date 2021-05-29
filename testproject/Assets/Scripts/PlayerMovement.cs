using System.Collections.Generic;
using MLAPI;
using UnityEngine;

public class PlayerMovement : NetworkBehaviour
{
    [SerializeField]
    private float m_Speed = 20.0f;
    [SerializeField]
    private float m_RotSpeed = 5.0f;
    private Rigidbody m_Rigidbody;

    public static Dictionary<ulong, PlayerMovement> Players = new Dictionary<ulong, PlayerMovement>();

    private void Start()
    {
        m_Rigidbody = GetComponent<Rigidbody>();
        if (IsLocalPlayer)
        {
            var temp = transform.position;
            temp.y = 0.5f;
            transform.position = temp;
        }

        if (m_Rigidbody)
        {
            // Only the owner should ever move an object
            // If we don't set the non-local-player object as kinematic,
            // the local physics would apply and result in unwanted position
            // updates being sent up
            m_Rigidbody.isKinematic = !IsLocalPlayer;
        }
    }

    public override void NetworkStart()
    {
        base.NetworkStart();
        Players[OwnerClientId] = this; // todo should really have a NetworkStop for unregistering this...
    }

    private void FixedUpdate()
    {
        if (IsLocalPlayer)
        {
            transform.position += Input.GetAxis("Vertical") * m_Speed * Time.fixedDeltaTime * transform.forward;
            transform.rotation = Quaternion.Euler(0, Input.GetAxis("Horizontal") * 90 * m_RotSpeed * Time.fixedDeltaTime, 0) * transform.rotation;
        }
    }
}
