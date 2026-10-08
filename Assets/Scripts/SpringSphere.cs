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
    private ConfigurableJoint travelLimit;
    private Vector3 restPosition;
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

    // Walking into the side of a raised sphere only pushes it sideways, which the joint ignores.
    // Turn that shove into a push into the surface so it sinks and lets the pusher step in.
    // The contact impulse is only sideways while something presses against it, so just touching it does nothing.
    void OnCollisionStay(Collision collision)
    {
        if (!joint || sphereType.sidePushSink <= 0f || !collision.rigidbody) return;

        float side = Vector3.Dot(collision.rigidbody.worldCenterOfMass - rb.position, normal);
        Vector3 dir = side >= 0f ? normal : -normal;

        float sideways = Vector3.ProjectOnPlane(collision.impulse, normal).magnitude / Time.fixedDeltaTime;
        rb.AddForce(-dir * sideways * sphereType.sidePushSink);
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

    // Sink speed is about load / damping, so dividing damping scales it directly
    private float Damping => sphereType.damping / sphereType.sinkSpeedMultiplier;

    // Sphere type lengths are in sphere diameters; this is one diameter in world units
    private float SphereSize
    {
        get
        {
            var spawner = GetComponentInParent<GridSpawner>();
            return spawner ? spawner.WorldSphereSize : sphereCollider.radius * 2f * transform.lossyScale.x;
        }
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
        // sharedMaterial, so edit-mode spawning doesn't leak a material copy per sphere
        if (sphereType.visualMaterial && TryGetComponent<MeshRenderer>(out var meshRenderer))
            meshRenderer.sharedMaterial = sphereType.visualMaterial;
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
        if (!joint)
        {
            restPosition = rb.position;
            joint = CreateJoint();
            travelLimit = CreateTravelLimit();
        }
        // A joint limit is symmetric around its anchor, so center it half the depth below rest:
        // the sphere can sink up to maxSinkDepth but never rise above where it started
        float half = sphereType.maxSinkDepth * SphereSize * 0.5f;
        travelLimit.connectedAnchor = restPosition - normal * half;
        travelLimit.linearLimit = new SoftJointLimit { limit = half };
        joint.xDrive = new JointDrive
        {
            positionSpring = sphereType.stiffness,
            positionDamper = Damping,
            maximumForce = float.MaxValue,
        };
    }

    // Ties this sphere to a neighbor so their heights along the normal never differ by more than maxNeighborOffset.
    // Below that they move freely; at it, the pushed sphere drags the neighbor along, which drags the next one,
    // so a deep push leaves a cone-shaped pit with each ring maxNeighborOffset higher than the one inside it.
    // neighborPull < 1 makes the limit a spring instead: each pulled sphere's own spring soaks up part of the pull,
    // so it fades ring by ring. At 0.5 the pull equals the sphere's own stiffness and roughly 40% of the
    // overshoot past the offset carries on to the next ring.
    public void LinkTo(SpringSphere other)
    {
        float maxOffset = sphereType.maxNeighborOffset * SphereSize;
        if (!joint || !other.joint || maxOffset <= 0f) return;

        // Only the slide axis is limited: the spheres' own joints already keep them in place sideways
        var link = gameObject.AddComponent<ConfigurableJoint>();
        link.connectedBody = other.rb;
        link.axis = joint.axis;
        link.secondaryAxis = joint.secondaryAxis;
        link.xMotion = ConfigurableJointMotion.Limited;
        link.linearLimit = new SoftJointLimit { limit = maxOffset };

        // Spring 0 = hard limit
        float p = sphereType.neighborPull;
        if (p >= 1f) return;
        float scale = p / (1f - p);
        link.linearLimitSpring = new SoftJointLimitSpring
        {
            spring = sphereType.stiffness * scale,
            damper = Damping * scale,
        };
    }

    // Slides only along the grid normal and springs back to rest. How far it can slide is the travel limit's job.
    private ConfigurableJoint CreateJoint()
    {
        var grid = GetComponentInParent<SpringGrid>();
        normal = grid ? grid.Normal : Vector3.up;
        Vector3 axis = transform.InverseTransformDirection(normal);

        var j = gameObject.AddComponent<ConfigurableJoint>();
        j.axis = axis;
        j.secondaryAxis = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up);
        j.xMotion = ConfigurableJointMotion.Free;
        j.yMotion = ConfigurableJointMotion.Locked;
        j.zMotion = ConfigurableJointMotion.Locked;
        j.angularXMotion = ConfigurableJointMotion.Locked;
        j.angularYMotion = ConfigurableJointMotion.Locked;
        j.angularZMotion = ConfigurableJointMotion.Locked;
        return j;
    }

    // Separate joint because the spring's rest point and the limit's center can't differ on one joint.
    // Only limits the slide; everything else is left to the main joint.
    private ConfigurableJoint CreateTravelLimit()
    {
        var j = gameObject.AddComponent<ConfigurableJoint>();
        j.axis = joint.axis;
        j.secondaryAxis = joint.secondaryAxis;
        j.autoConfigureConnectedAnchor = false;
        j.xMotion = ConfigurableJointMotion.Limited;
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
