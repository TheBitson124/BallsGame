using UnityEngine;

// A plain physics sphere a FunctionGrid bends under: no input of its own, it only falls, rolls and gets thrown
// around by the balls, like anything else with a Rigidbody.
// The grid needs a capsule to bend around, so the sphere is a CapsuleCollider no taller than it is wide.
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class FunctionGridSphereBody : MonoBehaviour, IFunctionGridBody
{
    [Tooltip("How hard slowing balls (FunctionSphereType.slowdown) brake its horizontal speed, per second at 100%")]
    public float slowdownDrag = 10f;
    [Tooltip("On: spins to match its speed while the balls hold it up, so it rolls instead of sliding. The balls have no friction to do it")]
    public bool roll = true;

    private Rigidbody rb;
    private CapsuleCollider capsule;
    private bool grounded;
    private bool groundContact;      // collected from the grid during the physics step
    private float slowdown;          // 0..1, from the balls under it
    private float slowdownContact;   // collected like groundContact

    public Rigidbody Body => rb ? rb : rb = GetComponent<Rigidbody>();
    public CapsuleCollider Capsule => capsule ? capsule : capsule = GetComponent<CapsuleCollider>();

    // Read at the start of the next FixedUpdate, like collisions
    public void ReportGround(bool onGround) => groundContact |= onGround;

    // The strongest slowdown this step wins
    public void ReportSlowdown(float amount) => slowdownContact = Mathf.Max(slowdownContact, Mathf.Clamp01(amount));

    // Added in the editor: the new capsule (2 tall, 0.5 radius) becomes a sphere 1 wide
    void Reset()
    {
        CapsuleCollider c = Capsule;
        c.height = c.radius * 2f;
    }

    void FixedUpdate()
    {
        grounded = groundContact;
        groundContact = false;
        slowdown = slowdownContact;
        slowdownContact = 0f;

        Vector3 velocity = Body.velocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);

        // Slowing balls drag it to a stop: mud swallows a roll, 100% holds it in place
        if (slowdown > 0f)
        {
            Vector3 slowed = horizontal * Mathf.Exp(-slowdown * slowdownDrag * Time.fixedDeltaTime);
            rb.AddForce(slowed - horizontal, ForceMode.VelocityChange);
            horizontal = slowed;
        }

        // Rolling without slipping: spin = up × velocity / radius
        if (roll && grounded)
        {
            float radius = Capsule.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z));
            if (radius > 1e-4f) rb.angularVelocity = Vector3.Cross(Vector3.up, horizontal) / radius;
        }
    }
}
