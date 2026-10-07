using UnityEngine;

// Rigidbody player: surfaces push it with real forces, so bounces leave it with real velocity.
// Input only steers the velocity with limited acceleration instead of overwriting it.
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class PhysicsPlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5.0f;
    [Tooltip("How fast input changes velocity on the ground, m/s per second")]
    public float groundAcceleration = 30.0f;
    [Tooltip("How fast input changes velocity in the air, m/s per second")]
    public float airAcceleration = 5.0f;
    public float jumpHeight = 1.5f;

    [Header("Camera Settings")]
    public Transform cameraTransform;
    public float mouseSensitivity = 2.0f;
    public float verticalLookLimit = 80.0f;

    private Rigidbody rb;
    private float cameraPitch;
    private float yaw;
    private Vector2 moveInput;
    private bool jumpRequested;
    private bool isGrounded;
    private bool groundContact;   // collected from collisions during the physics step

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        yaw = transform.eulerAngles.y;

        // Lock mouse cursor to game window
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        // Read input every frame, apply it in FixedUpdate
        yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
        moveInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        if (Input.GetButtonDown("Jump")) jumpRequested = true;

        if (cameraTransform == null) return;
        cameraPitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
        cameraPitch = Mathf.Clamp(cameraPitch, -verticalLookLimit, verticalLookLimit);
        cameraTransform.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
    }

    void FixedUpdate()
    {
        isGrounded = groundContact;
        groundContact = false;

        rb.MoveRotation(Quaternion.Euler(0f, yaw, 0f));

        // Steer horizontal velocity toward the input; anything else (e.g. a bounce) fades out at the same rate
        Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        Vector3 targetVelocity = (right * moveInput.x + forward * moveInput.y).normalized * moveSpeed;

        Vector3 velocity = rb.velocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
        float accel = isGrounded ? groundAcceleration : airAcceleration;
        Vector3 newHorizontal = Vector3.MoveTowards(horizontal, targetVelocity, accel * Time.fixedDeltaTime);
        rb.AddForce(newHorizontal - horizontal, ForceMode.VelocityChange);

        // Jump: initial velocity needed to reach jumpHeight, added on top of any upward push
        // (jumping while a bouncy surface throws you up goes higher instead of cutting the bounce)
        if (jumpRequested && isGrounded)
        {
            float jumpSpeed = Mathf.Sqrt(2f * -Physics.gravity.y * jumpHeight);
            float newUpSpeed = Mathf.Max(velocity.y, 0f) + jumpSpeed;
            rb.AddForce(Vector3.up * (newUpSpeed - velocity.y), ForceMode.VelocityChange);
        }
        jumpRequested = false;
    }

    void OnCollisionStay(Collision collision)
    {
        for (int i = 0; i < collision.contactCount; i++)
        {
            if (collision.GetContact(i).normal.y > 0.5f)
            {
                groundContact = true;
                return;
            }
        }
    }
}
