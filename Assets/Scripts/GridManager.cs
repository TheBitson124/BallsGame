using System.Collections.Generic;
using UnityEngine;

// Runs before the player so the player can ride this frame's sphere movement (GroundDeltaY)
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
    private float centerDepth;

    // Contact solution, calculated once from the entry speed
    private float startDepth;
    private float staticDepth;
    private float maxDepth;
    private float compressTime;
    private float settleRate;
    private float reboundSpeed;
    private float timer;

    private Collider[] hitBuffer = new Collider[64];
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

    private void OnSphereContact(GroundSphere sphere, float entrySpeed)
    {
        contactType = sphere.sphereType;

        float mass = playerController.mass;
        float g = -playerController.gravity;
        float k = contactType.stiffness;
        float springTime = Mathf.Sqrt(mass / k);

        // Weight alone sinks to staticDepth; the entry speed adds the impact sink on top
        staticDepth = Mathf.Min(mass * g / k, contactType.maxSinkDepth);
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
        if (phase != Phase.Idle && phase != Phase.Release && !playerController.OnSphere)
            phase = Phase.Release;

        // The dip follows the player while he stands on it
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
                        playerController.Launch(reboundSpeed);
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

        // Carry the player with the surface while he's standing on it
        if (phase != Phase.Idle && phase != Phase.Release)
            playerController.GroundDeltaY = prevDepth - centerDepth;

        if (phase != Phase.Idle)
            GatherNearby(center);
        if (activeSpheres.Count == 0) return;

        float riseSpeed = phase == Phase.Settle
            ? Mathf.Max(settleRate, contactType.releaseSpeed)
            : contactType.releaseSpeed;

        restedSpheres.Clear();
        foreach (var sphere in activeSpheres)
        {
            if (sphere.MoveToDepth(centerDepth * Influence(sphere), riseSpeed))
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

    // Adds spheres around pos to the active set (zero allocation query)
    private void GatherNearby(Vector3 pos)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(pos, effectRadius, hitBuffer, sphereLayer);
        for (int i = 0; i < hitCount; i++)
        {
            if (hitBuffer[i].TryGetComponent<GroundSphere>(out var sphere))
                activeSpheres.Add(sphere);
        }
    }

    // 1 at the dip center, smoothly falling to 0 at effectRadius
    private float Influence(GroundSphere sphere)
    {
        Vector3 p = sphere.transform.position;
        Vector2 offset = new Vector2(p.x - center.x, p.z - center.z);
        float x = Mathf.Clamp01(1f - offset.magnitude / effectRadius);
        return x * x * (3f - 2f * x);
    }
}
