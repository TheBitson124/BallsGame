using System.Collections.Generic;
using UnityEngine;

// Runs before the player so the player can ride this frame's sphere movement (AddGroundDelta)
[DefaultExecutionOrder(-10)]
public class GroundGridManager : MonoBehaviour
{
    [Header("Player Reference")]
    public Transform player;
    public SimplePlayerController playerController;

    [Header("Global Settings")]
    public float effectRadius = 3.5f;
    public LayerMask sphereLayer;

    private enum Phase { Idle, Compress, Settle, Hold, Release }

    private Phase phase = Phase.Idle;
    private SphereTypeScriptableObject contactType;
    private Vector3 center;
    private Vector3 pushAxis = Vector3.down;   // direction the dip goes into the surface
    private float centerDepth;

    // Contact solution, calculated once from the entry velocity
    private float startDepth;
    private float staticDepth;
    private float maxDepth;
    private float compressTime;
    private float settleRate;
    private float reboundSpeed;
    private float timer;

    // Large enough for where grids meet (e.g. floor + wall corner); overflow silently drops spheres
    private Collider[] hitBuffer = new Collider[256];
    private HashSet<GroundSphere> activeSpheres = new HashSet<GroundSphere>();
    private List<GroundSphere> restedSpheres = new List<GroundSphere>();

    void OnEnable()
    {
        if (playerController) playerController.SphereContact += OnSphereContact;
    }

    void OnDisable()
    {
        if (playerController) playerController.SphereContact -= OnSphereContact;
    }

    private void OnSphereContact(GroundSphere sphere, Vector3 entryVelocity)
    {
        // Several grids listen to the same player; only react to our own spheres
        if (sphere.transform.parent != transform) return;

        // The grid lies in this transform's local XZ plane, so the dip goes along transform.up.
        // The side the pusher is on decides the direction: from above pushes down, from below up.
        // (Contact normals can't be trusted for this: hitting a sphere's side gives a sideways normal.)
        Vector3 normal = transform.up;
        float side = Vector3.Dot(player.position - sphere.transform.position, normal);
        pushAxis = side >= 0f ? -normal : normal;

        contactType = sphere.sphereType;

        float mass = playerController.mass;
        float k = contactType.stiffness;
        float springTime = Mathf.Sqrt(mass / k);
        float entrySpeed = Mathf.Max(0f, Vector3.Dot(entryVelocity, pushAxis));
        // Only the part of gravity pressing into the surface counts: full on floors, none on walls
        float weightAccel = Mathf.Max(0f, Vector3.Dot(Vector3.up * playerController.gravity, pushAxis));

        // Weight alone sinks to staticDepth; the entry speed adds the impact sink on top
        staticDepth = Mathf.Min(mass * weightAccel / k, contactType.maxSinkDepth);
        maxDepth = Mathf.Min(staticDepth + entrySpeed * springTime, contactType.maxSinkDepth);
        compressTime = Mathf.PI * 0.5f * springTime; // quarter of a spring oscillation
        settleRate = (maxDepth - staticDepth) / contactType.settleTime;

        reboundSpeed = entrySpeed * contactType.material.bounciness;
        if (reboundSpeed < contactType.minReboundSpeed) reboundSpeed = 0f;

        center = player.position;
        startDepth = centerDepth;
        timer = 0f;
        phase = Phase.Compress;
    }

    void Update()
    {
        if (!player) return;

        float prevDepth = centerDepth;

        // Player left the spheres (jumped, launched, walked off): let the dip spring back
        if (phase != Phase.Idle && phase != Phase.Release && !playerController.IsTouching(transform))
            phase = Phase.Release;

        // The dip follows the player while he touches it
        if (phase != Phase.Idle && phase != Phase.Release)
            center = player.position;

        switch (phase)
        {
            case Phase.Compress:
                timer += Time.deltaTime;
                float t = Mathf.Clamp01(timer / compressTime);
                centerDepth = Mathf.Lerp(startDepth, maxDepth, Mathf.Sin(t * Mathf.PI * 0.5f));
                if (t >= 1f)
                {
                    if (reboundSpeed > 0f)
                    {
                        // Throw the player back out of the surface
                        playerController.Launch(-pushAxis * reboundSpeed);
                        phase = Phase.Release;
                    }
                    else
                    {
                        phase = Phase.Settle;
                    }
                }
                break;

            case Phase.Settle:
                centerDepth = Mathf.MoveTowards(centerDepth, staticDepth, settleRate * Time.deltaTime);
                if (centerDepth <= staticDepth) phase = Phase.Hold;
                break;

            case Phase.Hold:
                centerDepth = staticDepth;
                break;

            case Phase.Release:
                centerDepth = Mathf.MoveTowards(centerDepth, 0f, contactType.releaseSpeed * Time.deltaTime);
                if (centerDepth <= 0f) phase = Phase.Idle;
                break;
        }

        // Carry the player with the surface while he touches it
        if (phase != Phase.Idle && phase != Phase.Release)
            playerController.AddGroundDelta(pushAxis * (centerDepth - prevDepth));

        if (phase != Phase.Idle)
            GatherNearby(center);
        if (activeSpheres.Count == 0) return;

        float riseSpeed = phase == Phase.Settle
            ? Mathf.Max(settleRate, contactType.releaseSpeed)
            : contactType.releaseSpeed;

        restedSpheres.Clear();
        foreach (var sphere in activeSpheres)
        {
            if (sphere.MoveToDepth(centerDepth * Influence(sphere), pushAxis, riseSpeed))
                restedSpheres.Add(sphere);
        }

        // Remove rested spheres from active tracking loop
        for (int i = 0; i < restedSpheres.Count; i++)
        {
            activeSpheres.Remove(restedSpheres[i]);
        }

        // Let the CharacterController collide with the moved spheres this frame
        Physics.SyncTransforms();
    }

    // Adds this grid's spheres around pos to the active set (zero allocation query)
    private void GatherNearby(Vector3 pos)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(pos, effectRadius, hitBuffer, sphereLayer);
        for (int i = 0; i < hitCount; i++)
        {
            if (hitBuffer[i].TryGetComponent<GroundSphere>(out var sphere) && sphere.transform.parent == transform)
                activeSpheres.Add(sphere);
        }
    }

    // 1 at the dip center, smoothly falling to 0 at effectRadius, measured along the grid surface
    private float Influence(GroundSphere sphere)
    {
        Vector3 offset = Vector3.ProjectOnPlane(sphere.transform.position - center, pushAxis);
        float x = Mathf.Clamp01(1f - offset.magnitude / effectRadius);
        return x * x * (3f - 2f * x);
    }
}
