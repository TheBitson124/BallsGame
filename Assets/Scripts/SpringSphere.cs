using UnityEngine;

// A sphere on a spring along its grid's normal that gets pushed in by whatever lands on it.
// Dynamic Rigidbody on a ConfigurableJoint: PhysX solves the spring together with the contacts,
// so the pusher's mass, landing impacts and the rebound come out right and stay stable.
// maxSinkDepth 0 = rigid ground (kinematic, never moves).
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class SpringSphere : MonoBehaviour
{
    public SphereTypeScriptableObject sphereType;

    private static readonly Collider[] overlapBuffer = new Collider[32];

    private Rigidbody rb;
    private SphereCollider sphereCollider;
    private ConfigurableJoint joint;
    private Vector3 normal;          // world direction the sphere slides along
    private Rigidbody lander;       // what landed on it hard enough to be bounced back
    private Vector3 outDir;         // from the surface toward the lander's side
    private float landingSpeed;     // lander's speed into the surface at touchdown
    private Vector3 landingSlide;   // lander's speed along the surface at touchdown

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ApplyType();
    }

    void Start() => IgnoreStartingOverlaps();

    // Spheres ignore the floor and each other, so any contact is something landing on or walking over it
    void OnCollisionEnter(Collision collision)
    {
        if (!joint || sphereType.bounceRestitution <= 0f || !collision.rigidbody) return;

        float side = Vector3.Dot(collision.rigidbody.worldCenterOfMass - rb.position, normal);
        Vector3 dir = side >= 0f ? normal : -normal;

        // relativeVelocity is taken before the contact stopped the lander; flip it so it's the lander's
        // velocity relative to the sphere (heading into the surface)
        Vector3 rel = collision.relativeVelocity;
        if (Vector3.Dot(rel, dir) > 0f) rel = -rel;

        float speedIn = -Vector3.Dot(rel, dir);
        if (speedIn < sphereType.bounceMinLandingSpeed) return;

        lander = collision.rigidbody;
        outDir = dir;
        landingSpeed = speedIn;
        landingSlide = Vector3.ProjectOnPlane(rel, dir);
    }

    // The spring threw the lander back out: top it up to the bounce speed and give back the slide
    // speed the landing took. Max, so the other spheres it was on don't stack the top-up.
    void OnCollisionExit(Collision collision)
    {
        if (!lander || collision.rigidbody != lander) return;
        Rigidbody body = lander;
        lander = null;

        Vector3 v = body.velocity;
        float outSpeed = Vector3.Dot(v, outDir);
        if (outSpeed <= 0f) return;   // walked or slid off instead of bouncing

        Vector3 slide = v - outDir * outSpeed;
        if (landingSlide.sqrMagnitude > slide.sqrMagnitude) slide = landingSlide;
        outSpeed = Mathf.Max(outSpeed, landingSpeed * sphereType.bounceRestitution);
        body.velocity = slide + outDir * outSpeed;
    }

    public void SetType(SphereTypeScriptableObject type)
    {
        sphereType = type;
        ApplyType();
    }

    private void ApplyType()
    {
        // Awake doesn't run for objects spawned in edit mode, so fetch lazily
        if (!sphereCollider) sphereCollider = GetComponent<SphereCollider>();
        sphereCollider.sharedMaterial = sphereType.material;
        if (!Application.isPlaying || !rb) return;

        bool rigid = sphereType.maxSinkDepth <= 0f;
        rb.isKinematic = rigid;
        rb.useGravity = false;   // gravity would sag soft springs (mud) far below the surface
        rb.mass = sphereType.sphereMass;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // Mud rises at a few mm/s; the default threshold would put it to sleep halfway up
        rb.sleepThreshold = 1e-7f;
        if (rigid) return;

        // Created once at the rest position: the joint anchors to wherever the sphere is now
        if (!joint) joint = CreateJoint();
        joint.linearLimit = new SoftJointLimit { limit = sphereType.maxSinkDepth };
        joint.xDrive = new JointDrive
        {
            positionSpring = sphereType.stiffness,
            positionDamper = sphereType.damping,
            maximumForce = float.MaxValue,
        };
    }

    // Ties this sphere to a neighbor with a spring along the normal, so pushing one drags the other partway.
    // For a sphere between neighbors: dip = link * (sum of neighbor dips) / (stiffness + link * neighbors),
    // and link = stiffness * f / (1 - f)^2 makes each ring out sink about f times the ring before it.
    public void LinkTo(SpringSphere other)
    {
        float f = sphereType.neighborFalloff;
        if (!joint || !other.joint || f <= 0f) return;
        float scale = f / ((1f - f) * (1f - f));

        // Every axis left free: the spheres' own joints already keep them in place, this only adds the spring
        var link = gameObject.AddComponent<ConfigurableJoint>();
        link.connectedBody = other.rb;
        link.axis = joint.axis;
        link.secondaryAxis = joint.secondaryAxis;
        link.xDrive = new JointDrive
        {
            positionSpring = sphereType.stiffness * scale,
            positionDamper = sphereType.damping * scale,
            maximumForce = float.MaxValue,
        };
    }

    // Slides only along the grid normal, either way, so it can be pushed from both sides
    private ConfigurableJoint CreateJoint()
    {
        var grid = GetComponentInParent<SpringGrid>();
        normal = grid ? grid.Normal : Vector3.up;
        Vector3 axis = transform.InverseTransformDirection(normal);

        var j = gameObject.AddComponent<ConfigurableJoint>();
        j.axis = axis;
        j.secondaryAxis = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up);
        j.xMotion = ConfigurableJointMotion.Limited;
        j.yMotion = ConfigurableJointMotion.Locked;
        j.zMotion = ConfigurableJointMotion.Locked;
        j.angularXMotion = ConfigurableJointMotion.Locked;
        j.angularYMotion = ConfigurableJointMotion.Locked;
        j.angularZMotion = ConfigurableJointMotion.Locked;
        return j;
    }

    // Spheres are packed touching each other and can poke through a floor placed under the grid.
    // Depenetrating from those would fight the spring, so ignore everything static or sphere we start in.
    private void IgnoreStartingOverlaps()
    {
        Vector3 scale = transform.lossyScale;
        float radius = sphereCollider.radius * Mathf.Max(scale.x, scale.y, scale.z) * 1.05f;
        Vector3 center = transform.TransformPoint(sphereCollider.center);
        int count = Physics.OverlapSphereNonAlloc(center, radius, overlapBuffer, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider other = overlapBuffer[i];
            if (other == sphereCollider) continue;
            if (!other.attachedRigidbody || other.TryGetComponent<SpringSphere>(out _))
                Physics.IgnoreCollision(sphereCollider, other);
        }
    }
}
