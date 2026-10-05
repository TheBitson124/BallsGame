using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class SimplePlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5.0f;
    public float gravity = -9.81f;
    public float jumpHeight = 1.5f;

    [Header("Camera Settings")]
    public Transform cameraTransform;
    public float mouseSensitivity = 2.0f;
    public float verticalLookLimit = 80.0f;

    [Header("Grid Influence")]
    [Tooltip("Read by GroundGridManager: weight-only sink = mass * g / stiffness")]
    public float mass = 1.0f;

    // Fired once when the player first touches a sphere; passes the sphere and the
    // downward entry speed (m/s, 0 when walking on rather than falling on)
    public event System.Action<GroundSphere, float> SphereContact;
    public bool OnSphere => onSphere;
    // Vertical movement of the surface under the player this frame; set by GroundGridManager
    public float GroundDeltaY { get; set; }

    private CharacterController controller;
    private float cameraPitch = 0.0f;
    private float verticalVelocity = 0.0f;
    private bool onSphere;        // stood on a GroundSphere after the last Move
    private bool touchedSphere;   // collecting during the current Move
    private float entrySpeed;

    void Start()
    {
        controller = GetComponent<CharacterController>();

        // Lock mouse cursor to game window
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleRotation();
        HandleMovement();
    }

    private void HandleRotation()
    {
        if (cameraTransform == null) return;

        // Mouse look inputs
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        // Rotate character body horizontally
        transform.Rotate(Vector3.up * mouseX);

        // Tilt camera vertically with clamping
        cameraPitch -= mouseY;
        cameraPitch = Mathf.Clamp(cameraPitch, -verticalLookLimit, verticalLookLimit);
        cameraTransform.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
    }

    private void HandleMovement()
    {
        // WASD inputs
        float inputX = Input.GetAxisRaw("Horizontal");
        float inputZ = Input.GetAxisRaw("Vertical");

        Vector3 moveDirection = (transform.right * inputX + transform.forward * inputZ).normalized;

        // Apply simple gravity
        if (controller.isGrounded && verticalVelocity < 0)
        {
            verticalVelocity = -2.0f; // Small grounding force
        }
        else
        {
            verticalVelocity += gravity * Time.deltaTime;
        }

        // Jump: initial velocity needed to reach jumpHeight
        if (controller.isGrounded && Input.GetButtonDown("Jump"))
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        // Only a fall counts as entry speed; walking onto spheres is weight only
        entrySpeed = controller.isGrounded ? 0f : Mathf.Max(0f, -verticalVelocity);
        touchedSphere = false;

        // Combine horizontal movement with gravity, plus the surface moving under us
        Vector3 finalVelocity = (moveDirection * moveSpeed) + (Vector3.up * verticalVelocity);
        controller.Move(finalVelocity * Time.deltaTime + Vector3.up * GroundDeltaY);
        GroundDeltaY = 0f;

        onSphere = touchedSphere;
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (touchedSphere || hit.normal.y < 0.5f) return;
        if (!hit.collider.TryGetComponent<GroundSphere>(out var sphere)) return;

        touchedSphere = true;
        if (!onSphere)
            SphereContact?.Invoke(sphere, entrySpeed);
    }

    // Throws the player upwards, e.g. on a sphere rebound
    public void Launch(float upSpeed)
    {
        verticalVelocity = upSpeed;
    }
}