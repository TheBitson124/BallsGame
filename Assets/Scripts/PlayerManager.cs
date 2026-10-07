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
    [Tooltip("How fast sideways launch momentum fades, in m/s per second")]
    public float momentumDrag = 5.0f;

    // Fired once when the player first touches a sphere grid; passes the sphere hit
    // and the player's velocity at contact
    public event System.Action<GroundSphere, Vector3> SphereContact;

    private CharacterController controller;
    private float cameraPitch = 0.0f;
    private float verticalVelocity = 0.0f;
    private Vector3 externalVelocity;   // momentum from launches, fades out
    private Vector3 groundDelta;        // movement of the surfaces we touch this frame
    private Vector3 entryVelocity;
    private List<Transform> touchedGrids = new List<Transform>();   // touched during the last Move
    private List<Transform> touchingGrids = new List<Transform>();  // collecting during the current Move

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

        externalVelocity = Vector3.MoveTowards(externalVelocity, Vector3.zero, momentumDrag * Time.deltaTime);

        // Input can steer around launch momentum but not push against it,
        // otherwise holding forward cancels a bounce off a wall
        Vector3 inputVelocity = moveDirection * moveSpeed;
        if (externalVelocity.sqrMagnitude > 0.0001f)
        {
            Vector3 launchDir = externalVelocity.normalized;
            float against = Vector3.Dot(inputVelocity, launchDir);
            if (against < 0f) inputVelocity -= launchDir * against;
        }
        Vector3 horizontalVelocity = inputVelocity + externalVelocity;

        // Velocity we hit surfaces with; the small grounding force doesn't count as a hit
        entryVelocity = horizontalVelocity + Vector3.up * (controller.isGrounded ? 0f : verticalVelocity);
        touchingGrids.Clear();

        // Combine horizontal movement with gravity, plus the surfaces moving under us
        Vector3 finalVelocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(finalVelocity * Time.deltaTime + groundDelta);
        groundDelta = Vector3.zero;

        // This Move's contacts become "last Move" for the next frame
        (touchedGrids, touchingGrids) = (touchingGrids, touchedGrids);
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (!hit.collider.TryGetComponent<GroundSphere>(out var sphere)) return;

        Transform grid = sphere.transform.parent;
        if (touchingGrids.Contains(grid)) return;

        touchingGrids.Add(grid);
        if (!touchedGrids.Contains(grid))
            SphereContact?.Invoke(sphere, entryVelocity);
    }

    // True while the player touches any sphere of this grid
    public bool IsTouching(Transform grid) => touchedGrids.Contains(grid);

    // Moves the player along with a surface he touches; summed over all grids each frame
    public void AddGroundDelta(Vector3 delta) => groundDelta += delta;

    // Replaces the player's speed along the launch direction, keeps the rest
    public void Launch(Vector3 velocity)
    {
        Vector3 dir = velocity.normalized;
        Vector3 current = externalVelocity + Vector3.up * verticalVelocity;
        current += velocity - dir * Vector3.Dot(current, dir);

        verticalVelocity = current.y;
        externalVelocity = new Vector3(current.x, 0f, current.z);
    }
}