using UnityEngine;
using UnityEngine.Rendering;

// A grid of balls with no physics at all: no Rigidbody, no collider, no GameObject per ball.
// The player's capsule is the only collider. The player bends a height function and the balls follow its samples;
// this grid stands in for their colliders by keeping the player's velocity out of them.
// All balls are drawn in one instanced call with one material. A ball's color is its type: painting a color turns it
// into the type with that color, physics and all.
[ExecuteAlways]
[DefaultExecutionOrder(100)]
public class FunctionGrid : MonoBehaviour
{
    [Header("Grid")]
    public int rows = 40;
    public int columns = 40;
    [Tooltip("Ball diameter, in this object's local units")]
    [Min(0.001f)] public float sphereSize = 1f;
    [Tooltip("Distance between ball centers, in ball diameters. 1 = touching, no gaps or overlaps")]
    [Min(0.001f)] public float spacing = 1f;
    public FunctionSphereType sphereType;

    [Header("Look")]
    [Tooltip("Mesh of one ball, 1 unit wide. Empty = Unity's built-in sphere")]
    public Mesh ballMesh;
    [Tooltip("Draws every ball. Needs the FunctionBalls/Ball shader")]
    public Material ballMaterial;

    [Header("Paint")]
    [Tooltip("Types paint can turn a ball into. A color paints the type whose color is closest. sphereType is always one of them")]
    public FunctionSphereType[] paintTypes;
    [Tooltip("Types the grid starts with, stretched over it: pixel x = column, y = row. Each pixel paints its ball (see paintTypes); transparent pixels leave sphereType. Needs Read/Write enabled in its import settings")]
    public Texture2D paintMap;

    [Header("Player")]
    [Tooltip("Empty = the first one in the scene")]
    public PhysicsPlayerController player;
    [Tooltip("Gap to a ball under the player that still counts as standing on it, in local units")]
    [Min(0f)] public float touchDistance = 0.05f;

    [Header("Debug")]
    [Tooltip("Logs to the console whenever this grid flattens its hole or resets the player's run of bounces, with the reason and what it had reached")]
    public bool logResets = true;

    // Share of an overlap pushed out per physics step. 1 = all at once, which overshoots into a small bounce
    private const float PushOut = 0.3f;
    private const int SolverIterations = 4;
    // Speed out of the surface (grid units/s) beyond what the grid itself gave the player that counts as a jump
    private const float JumpSpeed = 1f;

    // Distance between neighboring ball centers, in local units
    public float CellSize => spacing * sphereSize;

    // Flat per-ball arrays, index = x + z * columns
    private FunctionSphereType[] types;
    private float[] height;           // local y, 0 = rest
    private float[] previousHeight;   // last step's, for drawing between physics steps
    private Color[] colors;           // what's drawn: each ball's type color
    private Vector4[] drawData;       // xyz = local position, w = diameter
    private Vector3 offset;
    private Rigidbody playerBody;
    private CapsuleCollider playerCapsule;

    // Drawing
    private GraphicsBuffer ballBuffer;
    private GraphicsBuffer colorBuffer;
    private MaterialPropertyBlock drawProperties;
    private bool colorsChanged;
    private bool rebuild;
    private float deepest;            // deepest any ball's type goes, for the draw bounds
    private float highest;            // highest any ball's type rises above rest, for the draw bounds

    // The hole under the player, in local units: how deep it is right under them, and what it's doing
    private enum Phase { Settle, Dip, Bounce }
    private Phase phase;
    private float holeDepth;
    private float holeSpeed;          // into the surface; negative while it rises
    private float holeAccel;          // into the surface; negative slows a dip, positive speeds up a bounce
    private float dipTarget;
    private float launchSpeed;
    private bool touching;            // standing on the balls at the start of the last step
    private Vector3 holeCenter;       // grid space; follows the player, stays put while waiting for the reset
    private bool waitingForReset;     // the player left: the hole goes back to rest where it was before resetting
    private bool wasTouching;
    private bool carriedLastStep;     // last step set the player's speed out of the surface (dip or launch)
    private float rideSpeed;          // how fast the balls the player stood on rose last step, grid units/s
    private float lastSetSpeed;       // vertical speed this grid left the player with last step, grid units/s
    private float fallSpeed;          // speed into the surface during the last step in the air

    // Run of bounces: starts with the first landing on a type and lasts until the player leaves the grid.
    // Its bounce starts at the type's and fades to 1 over the type's maxBounceCount bounces.
    private FunctionSphereType runType;    // type the run is on; null = no run
    private float runBounce;               // bounce for the next landing of the run
    private float runLaunch;               // last launch speed of the run, grid units/s
    private bool runLastCounted;           // the last landing was fast enough to count toward maxBounceCount

    private int Count => types != null ? types.Length : 0;
    private Mesh Mesh => ballMesh ? ballMesh : Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

    // Changes one ball's type at runtime, e.g. a mud patch inside solid ground. It takes the type's color with it
    public void SetType(int x, int z, FunctionSphereType type)
    {
        int i = x + z * columns;
        types[i] = type;
        deepest = Mathf.Max(deepest, type.DeepestDepth);
        highest = Mathf.Max(highest, type.HighestRise);
        UpdateColor(i);
    }

    // Paints one ball: it becomes the paintable type whose color is closest
    public void Paint(int x, int z, Color color) => SetType(x, z, TypeForColor(color));

    // Back to the grid's own sphereType
    public void ClearPaint(int x, int z) => SetType(x, z, sphereType);

    // The paintable type (paintTypes or sphereType) whose color is closest to this one
    public FunctionSphereType TypeForColor(Color color)
    {
        FunctionSphereType best = sphereType;
        float bestDistance = best ? ColorDistance(best.color, color) : float.MaxValue;
        if (paintTypes == null) return best;
        foreach (FunctionSphereType type in paintTypes)
        {
            if (!type) continue;
            float distance = ColorDistance(type.color, color);
            if (distance < bestDistance)
            {
                best = type;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static float ColorDistance(Color a, Color b)
    {
        float r = a.r - b.r, g = a.g - b.g, bl = a.b - b.b;
        return r * r + g * g + bl * bl;
    }

    void OnEnable()
    {
        Camera.onPreCull += Draw;
        Build();
    }

    void OnDisable()
    {
        Camera.onPreCull -= Draw;
        ReleaseBuffers();
    }

    // Edit mode: any change rebuilds, so the scene view shows it. Playing: only a new grid size does,
    // so tweaking settings doesn't wipe runtime paint and types
    void OnValidate()
    {
        if (!Application.isPlaying || rows * columns != Count) rebuild = true;
#if UNITY_EDITOR
        if (!ballMaterial) ballMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/FunctionBall.mat");
#endif
    }

    // The balls are only drawn, so the Scene view has nothing to click on. An invisible box over the balls at rest
    // gives it something: clicking anywhere on the grid selects it (Gizmos must be on in the Scene view).
    void OnDrawGizmos()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = Color.clear;
        Gizmos.DrawCube(Vector3.zero, GridSize);
    }

    // Outline while selected, to see the area the grid covers
    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, GridSize);
    }

    // Local size of the balls at rest, edge to edge
    private Vector3 GridSize =>
        new Vector3((columns - 1) * CellSize + sphereSize, sphereSize, (rows - 1) * CellSize + sphereSize);

    void Start()
    {
        if (!Application.isPlaying) return;
        if (!sphereType)
        {
            Debug.LogError($"{name}: FunctionGrid needs a sphere type", this);
            enabled = false;
            return;
        }
        if (!ballMaterial) Debug.LogError($"{name}: FunctionGrid needs a ball material (FunctionBalls/Ball shader)", this);
        RemoveOldBallObjects();

        if (!player) player = FindObjectOfType<PhysicsPlayerController>();
        if (player)
        {
            playerBody = player.GetComponent<Rigidbody>();
            playerCapsule = player.GetComponent<CapsuleCollider>();
        }
    }

    // Fresh grid: every ball of sphereType, or the type its paintMap pixel paints, at rest
    private void Build()
    {
        rebuild = false;
        ReleaseBuffers();
        int count = Mathf.Max(0, rows * columns);
        offset = new Vector3((columns - 1) * CellSize * 0.5f, 0f, (rows - 1) * CellSize * 0.5f);
        types = new FunctionSphereType[count];
        height = new float[count];
        previousHeight = new float[count];
        colors = new Color[count];
        drawData = new Vector4[count];
        phase = Phase.Settle;
        holeDepth = 0f;
        touching = wasTouching = carriedLastStep = false;
        EndRun("grid rebuilt");
        waitingForReset = false;
        rideSpeed = 0f;
        lastSetSpeed = 0f;
        deepest = sphereType ? sphereType.DeepestDepth : 0f;
        highest = sphereType ? sphereType.HighestRise : 0f;

        bool readable = paintMap && paintMap.isReadable;
        if (paintMap && !readable)
            Debug.LogWarning($"{name}: paint map {paintMap.name} needs Read/Write enabled in its import settings", this);

        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < columns; x++)
            {
                int i = x + z * columns;
                types[i] = readable ? PaintMapType(x, z) : sphereType;
                if (types[i])
                {
                    deepest = Mathf.Max(deepest, types[i].DeepestDepth);
                    highest = Mathf.Max(highest, types[i].HighestRise);
                }
                UpdateColor(i);
            }
        }
        if (count == 0) return;

        ballBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 4);
        colorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 4);
        drawProperties ??= new MaterialPropertyBlock();
        drawProperties.SetBuffer("_Balls", ballBuffer);
        drawProperties.SetBuffer("_Colors", colorBuffer);
        UploadBalls(1f);
    }

    private void ReleaseBuffers()
    {
        ballBuffer?.Release();
        colorBuffer?.Release();
        ballBuffer = colorBuffer = null;
    }

    // Nearest pixel, not filtered: a blend of two colors at a border could pick a third type
    private FunctionSphereType PaintMapType(int x, int z)
    {
        int px = Mathf.Min(paintMap.width - 1, (int)((x + 0.5f) / columns * paintMap.width));
        int pz = Mathf.Min(paintMap.height - 1, (int)((z + 0.5f) / rows * paintMap.height));
        Color pixel = paintMap.GetPixel(px, pz);
        return pixel.a < 0.5f ? sphereType : TypeForColor(pixel);
    }

    private void UpdateColor(int i)
    {
        Color color = types[i] ? types[i].color : Color.white;
        color.a = 1f;
        colors[i] = color;
        colorsChanged = true;
    }

    // The old version made a GameObject per ball; they'd sit unmoving on top of the drawn balls
    [ContextMenu("Remove Old Ball Objects")]
    public void RemoveOldBallObjects()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;
            if (!child.name.StartsWith("Ball_")) continue;
            if (Application.isPlaying) Destroy(child);
#if UNITY_EDITOR
            else UnityEditor.Undo.DestroyObjectImmediate(child);
#endif
        }
    }

    void FixedUpdate()
    {
        if (Count == 0 || !Application.isPlaying) return;
        System.Array.Copy(height, previousHeight, height.Length);

        if (!GetCapsule(out Vector3 a, out Vector3 b, out float radius))
        {
            System.Array.Clear(height, 0, height.Length);
            EndRun("no player to follow");
            ResetHole("no player to follow");
            waitingForReset = false;
            touching = wasTouching = carriedLastStep = false;
            rideSpeed = 0f;
            lastSetSpeed = 0f;
            return;
        }

        // Leaving the grid starts the wait for its reset: the hole stays where the player left it and goes back to
        // rest there (see SettleBack). Already at rest, there's nothing to wait for.
        if (!waitingForReset && !OverGrid(a))
        {
            if (holeDepth != 0f || phase != Phase.Settle)
            {
                waitingForReset = true;
                if (logResets)
                    Debug.Log($"[{name}] player left the grid: waiting for the balls to go back to rest ({holeDepth:0.###} deep)", this);
            }
            else EndRun("player left the grid");
        }

        float oldDepth = holeDepth;
        FunctionSphereType ground = GroundType(a);
        bool carry = false;
        float carrySpeed = 0f;
        if (waitingForReset) SettleBack();
        else
        {
            holeCenter = a;
            carry = UpdateHole(ground, OverGrid(a), out carrySpeed);
        }

        // Balls only give way as far as the player presses in: the player's lowest point below the tops of
        // balls at rest. In the air, or standing on something above the grid, nothing presses and the grid is flat.
        // A hole left behind while waiting for the reset isn't the player's any more, so it shows in full.
        float press = waitingForReset ? float.MaxValue : Mathf.Max(0f, sphereSize * 0.5f - (a.y - radius));
        for (int i = 0; i < height.Length; i++)
            height[i] = -Mathf.Min(HoleDepth(i, holeCenter, holeDepth), press);

        SolveContacts(a, b, radius, GroundType(holeCenter), oldDepth, carry, carrySpeed);
        if (touching) player.ReportSlowdown(ground.playerSlowdown);
    }

    // Moves the hole one step. A landing dips it, then it comes back up to restDepth: slowly, or for a bouncy type
    // faster and faster so it throws the player out at the end.
    // Returns true to set the player's speed out of the surface this step: pulled down with the dip (the contacts
    // hold them back to the speed of the balls they actually rest on, which sink slower off-center), and launched
    // at the top of a bounce.
    private bool UpdateHole(FunctionSphereType ground, bool overGrid, out float carrySpeed)
    {
        float dt = Time.fixedDeltaTime;
        carrySpeed = 0f;
        bool landed = touching && !wasTouching;
        wasTouching = touching;

        // Jumped or walked off mid-dip: the hole just settles, and the player keeps whatever speed they left with
        if (!touching) phase = Phase.Settle;

        if (landed && fallSpeed > 0f && fallSpeed >= ground.minLandingSpeed)
        {
            float target = Mathf.Lerp(ground.restDepth, ground.landingDepth, fallSpeed / ground.fullLandingSpeed);
            if (target > holeDepth + 1e-4f)
            {
                // Slows the player from their landing speed to a stop exactly at the bottom of the dip
                phase = Phase.Dip;
                dipTarget = target;
                holeSpeed = fallSpeed;
                holeAccel = -fallSpeed * fallSpeed / (2f * (target - holeDepth));
                launchSpeed = BounceLaunch(ground, fallSpeed);
            }
        }

        switch (phase)
        {
            case Phase.Dip:
                holeSpeed += holeAccel * dt;
                holeDepth += holeSpeed * dt;
                if (holeSpeed > 0f && holeDepth < dipTarget)
                {
                    carrySpeed = -holeSpeed;
                    return true;
                }
                holeDepth = dipTarget;
                holeSpeed = 0f;
                // Speeds up from a stop at the bottom to launchSpeed exactly at restDepth
                if (launchSpeed > 0f && holeDepth > ground.restDepth)
                {
                    phase = Phase.Bounce;
                    holeAccel = -launchSpeed * launchSpeed / (2f * (holeDepth - ground.restDepth));
                }
                else phase = Phase.Settle;
                return true;

            case Phase.Bounce:
                // The rising balls push the player up through the contacts
                holeSpeed += holeAccel * dt;
                holeDepth += holeSpeed * dt;
                if (holeDepth > ground.restDepth) return false;
                // Back at the top: the hole stops and the player flies on
                holeDepth = ground.restDepth;
                holeSpeed = 0f;
                phase = Phase.Settle;
                carrySpeed = launchSpeed;
                return true;

            default:
                // In the air above a type that holds its shape (a raised platform jumped on): stay put
                if (!touching && overGrid && ground.holdWhileAirborne) return false;
                // Nothing on it (in the air above it, too): back to flat.
                // Moving away from flat (sinking in, or rising into a mound for a negative restDepth) goes at
                // sinkSpeed; moving back toward flat or restDepth at riseSpeed
                float rest = touching ? ground.restDepth : 0f;
                bool awayFromFlat = rest != 0f && Mathf.Sign(rest - holeDepth) == Mathf.Sign(rest);
                float speed = awayFromFlat ? ground.sinkSpeed : ground.riseSpeed;
                holeDepth = Mathf.MoveTowards(holeDepth, rest, speed * dt);
                return false;
        }
    }

    // Launch speed for a landing. A run of bounces starts at the type's bounce, and every counted bounce cuts
    // what's left above 1 until it reaches 1 after maxBounceCount: bouncing climbs quickly at first, then holds.
    // Only landings at minBounceSpeed or faster count. Slower ones bounce at the current value without using it up,
    // so a run that starts with small hops isn't spent before it gets going.
    // A counted landing right after a counted launch is measured against that launch, so jumping on each bounce
    // can't keep pushing the player higher. Anything else counts in full: the run's first landing (coming in from
    // higher ground sets a higher height to hold) and the first landing fast enough to count after small hops.
    private float BounceLaunch(FunctionSphereType ground, float landingSpeed)
    {
        if (runType != ground)
        {
            if (runType) EndRun($"landed on {ground.name}");
            runType = ground;
            runBounce = ground.bounce;
            runLastCounted = false;
        }

        bool counts = landingSpeed >= ground.minBounceSpeed;
        if (counts && runLastCounted) landingSpeed = Mathf.Min(landingSpeed, runLaunch);
        runLaunch = landingSpeed * runBounce;
        if (counts) runBounce = ground.NextBounce(runBounce);
        runLastCounted = counts;
        return runLaunch;
    }

    // Waiting for the reset after the player left: the hole goes back to rest where it was left, at its type's
    // riseSpeed, and nothing the player does changes it. Once it's flat every ball is back at rest, and the grid
    // resets: the run of bounces ends and the hole follows the player again, starting flat (so a raised platform
    // only grows once the player stands on it again). Ball types and colors are never touched.
    private void SettleBack()
    {
        wasTouching = touching;   // no landing is missed or invented when the hole follows the player again
        phase = Phase.Settle;
        holeSpeed = 0f;
        holeDepth = Mathf.MoveTowards(holeDepth, 0f, GroundType(holeCenter).riseSpeed * Time.fixedDeltaTime);
        if (holeDepth != 0f) return;

        waitingForReset = false;
        if (logResets) Debug.Log($"[{name}] all balls back at rest: grid reset", this);
        EndRun("player left the grid");
    }

    // Flattens the hole under the player at once and drops any dip or bounce in progress
    private void ResetHole(string reason)
    {
        if (holeDepth == 0f && phase == Phase.Settle) return;
        if (logResets)
            Debug.Log($"[{name}] hole reset to flat: {reason}. It was {holeDepth:0.###} deep ({phase})", this);
        holeDepth = 0f;
        holeSpeed = 0f;
        phase = Phase.Settle;
    }

    // Ends the run of bounces, so the next landing starts again from its type's full bounce
    private void EndRun(string reason)
    {
        if (!runType) return;
        if (logResets)
            Debug.Log($"[{name}] bounce run on {runType.name} reset: {reason}. Bounce had reached {runBounce:0.###} (starts at {runType.bounce:0.###}), last launch {runLaunch:0.##} units/s", this);
        runType = null;
    }

    private float HoleDepth(int i, Vector3 player, float depth)
    {
        Vector3 p = CellPosition(i % columns, i / columns);
        FunctionSphereType type = types[i];
        return type.Falloff(p.x - player.x, p.z - player.z) * type.ClampDepth(depth);
    }

    // Whether a point is above the grid's balls (not past its edge), in grid space
    private bool OverGrid(Vector3 p) =>
        Mathf.Abs(p.x) <= offset.x + CellSize * 0.5f && Mathf.Abs(p.z) <= offset.z + CellSize * 0.5f;

    // Type of the ball under the player's center; off the grid, the nearest edge ball's
    private FunctionSphereType GroundType(Vector3 player)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt((player.x + offset.x) / CellSize), 0, columns - 1);
        int z = Mathf.Clamp(Mathf.RoundToInt((player.z + offset.z) / CellSize), 0, rows - 1);
        return types[x + z * columns];
    }

    // Balls move in physics steps; draw them between the last two, like Rigidbody interpolation does for the player
    void Update()
    {
        if (rebuild) Build();
        if (ballBuffer == null) return;
        if (Application.isPlaying) UploadBalls(Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime));
    }

    // Contacts are against the hole, not the drawn heights: the balls give way freely until the hole
    // is as deep as it is right now, and only there hold the player up.
    // Works on velocity like PhysX's own solver, with each ball moving from its place in oldDepth's hole to the
    // current one this step: speed that would carry the player into a ball by the end of this step is removed,
    // and an existing overlap is pushed out a share per step on top of following the ball.
    // Balls the player could only reach this step count too, so a fast fall can't pass through between steps.
    // On top of the real balls, the function's own peak right under the player holds them up like one more ball,
    // so they stand at exactly restDepth (−100 = 100 up) wherever they are between ball centers. That only matters
    // for a mound, where every real ball is off the peak and lower; in a hole the balls around are higher anyway.
    // carry: the player rides the surface this step, moving out of it at carrySpeed (landing dip and bounce)
    private void SolveContacts(Vector3 a, Vector3 b, float radius, FunctionSphereType ground, float oldDepth, bool carry, float carrySpeed)
    {
        float scale = transform.lossyScale.x;
        float dt = Time.fixedDeltaTime;
        // Gravity is added after FixedUpdate; include it so a player at rest stays exactly at rest
        Vector3 worldVelocity = playerBody.velocity + (playerBody.useGravity ? Physics.gravity * dt : Vector3.zero);
        Vector3 v = transform.InverseTransformDirection(worldVelocity) / scale;   // grid units per second
        float speedIn = -v.y;
        bool overGrid = OverGrid(a);
        float peakFrom = -ground.ClampDepth(oldDepth);
        float peakSpeed = (-ground.ClampDepth(holeDepth) - peakFrom) / dt;

        // A jump the controller added last step is in the velocity now: compared to the speed this grid left the
        // player with last step (plus gravity since), they move out of the surface clearly faster. Then let them go
        // instead of pulling them back into the dip (or, at the top of a bounce, they already go faster than the
        // launch). Not compared to the carry itself: on a landing the contacts stop the player a step before the
        // dip starts pulling, and that difference isn't a jump.
        float gravityStep = playerBody.useGravity ? transform.InverseTransformDirection(Physics.gravity * dt).y / scale : 0f;
        bool jumped = v.y > lastSetSpeed + gravityStep + JumpSpeed;
        if (carry && jumped)
        {
            carry = false;
            if (phase == Phase.Dip) phase = Phase.Settle;
        }
        // Velocity change, so a jump the controller added this step still goes on top
        else if (carry) v.y = carrySpeed;
        // Riding the surface: the balls under the player rose at rideSpeed last step and carried them along.
        // Take back whatever part of it the ground no longer moves at, so when a rising mound (or a bounce settling
        // back) stops, they stop with it instead of flying on. Only that ground speed is taken back: a jump is on
        // top of it in the velocity and stays. Launches (carry) aren't taken back either.
        else if (touching && !carriedLastStep && overGrid) v.y -= Mathf.Max(0f, rideSpeed - Mathf.Max(0f, peakSpeed));
        carriedLastStep = carry;

        float reach = radius + sphereSize * 0.5f;
        float margin = v.magnitude * dt + touchDistance;
        if (!CellRange(a, b, reach + margin, out int x0, out int x1, out int z0, out int z1))
        {
            touching = false;
            fallSpeed = speedIn;
            rideSpeed = 0f;
            lastSetSpeed = v.y;
            return;
        }

        bool grounded = false;
        float supportSpeed = 0f;   // how fast the balls the player stands on rise this step
        for (int iteration = 0; iteration < SolverIterations; iteration++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = x + z * columns;
                    Vector3 s = CellPosition(x, z);
                    s.y = -HoleDepth(i, holeCenter, oldDepth);
                    Contact(s, (-HoleDepth(i, holeCenter, holeDepth) - s.y) / dt);
                }
            }
            if (OverGrid(holeCenter)) Contact(new Vector3(holeCenter.x, peakFrom, holeCenter.z), peakSpeed);
        }

        // One ball at s, moving up at ballSpeed this step
        void Contact(Vector3 s, float ballSpeed)
        {
            Vector3 d = ClosestOnSegment(a, b, s) - s;
            float dist = d.magnitude;
            float gap = dist - reach;
            if (gap > margin) return;

            Vector3 n = dist > 1e-5f ? d / dist : Vector3.up;
            if (gap <= touchDistance && transform.TransformDirection(n).y > 0.5f)
            {
                grounded = true;
                supportSpeed = Mathf.Max(supportSpeed, ballSpeed);
            }

            // Close the gap no faster than it ends this step; inside a ball, move out instead
            float minSpeed = (gap > 0f ? -gap / dt : -gap * PushOut / dt) + ballSpeed * n.y;
            float speed = Vector3.Dot(v, n);
            if (speed < minSpeed) v += n * (minSpeed - speed);
        }

        // The ground's own speed, not the player's: a jump is in the player's velocity from the next step on
        // and must not count as speed the balls gave them
        rideSpeed = supportSpeed;
        lastSetSpeed = v.y;   // before a jump this step: that shows up in the velocity next step
        playerBody.AddForce(transform.TransformDirection(v) * scale - worldVelocity, ForceMode.VelocityChange);
        player.ReportGround(grounded);

        touching = grounded;
        if (!grounded) fallSpeed = speedIn;
    }

    private void UploadBalls(float blend)
    {
        for (int i = 0; i < drawData.Length; i++)
        {
            Vector3 p = CellPosition(i % columns, i / columns);
            drawData[i] = new Vector4(p.x, Mathf.Lerp(previousHeight[i], height[i], blend), p.z, sphereSize);
        }
        ballBuffer.SetData(drawData);
    }

    // Runs for every camera about to render, scene view included, so the grid shows in edit mode too
    private void Draw(Camera camera)
    {
        if (rebuild) Build();
        if (ballBuffer == null || !ballMaterial) return;
        if (colorsChanged)
        {
            colorBuffer.SetData(colors);
            colorsChanged = false;
        }

        drawProperties.SetMatrix("_GridToWorld", transform.localToWorldMatrix);
        drawProperties.SetMatrix("_WorldToGrid", transform.worldToLocalMatrix);
        var renderParams = new RenderParams(ballMaterial)
        {
            camera = camera,
            layer = gameObject.layer,
            matProps = drawProperties,
            shadowCastingMode = ShadowCastingMode.On,
            receiveShadows = true,
            worldBounds = WorldBounds(),
        };
        Graphics.RenderMeshPrimitives(renderParams, Mesh, 0, Count);
    }

    // Everything the balls can reach: the grid, from the deepest hole up to the tops of balls on the highest mound
    private Bounds WorldBounds()
    {
        float r = sphereSize * 0.5f;
        Vector3 min = new Vector3(-offset.x - r, -deepest - r, -offset.z - r);
        Vector3 max = new Vector3(offset.x + r, highest + r, offset.z + r);
        var bounds = new Bounds(transform.TransformPoint(min), Vector3.zero);
        for (int corner = 1; corner < 8; corner++)
        {
            Vector3 p = new Vector3((corner & 1) != 0 ? max.x : min.x, (corner & 2) != 0 ? max.y : min.y, (corner & 4) != 0 ? max.z : min.z);
            bounds.Encapsulate(transform.TransformPoint(p));
        }
        return bounds;
    }

    // Centers of the player capsule's two hemispheres (a = bottom) and its radius, in grid space
    private bool GetCapsule(out Vector3 a, out Vector3 b, out float radius)
    {
        a = b = Vector3.zero;
        radius = 0f;
        if (!playerBody || !playerCapsule || !playerCapsule.enabled) return false;

        int dir = playerCapsule.direction;
        Vector3 scale = playerCapsule.transform.lossyScale;
        float radiusScale = Mathf.Max(Mathf.Abs(scale[(dir + 1) % 3]), Mathf.Abs(scale[(dir + 2) % 3]));
        float worldRadius = playerCapsule.radius * radiusScale;
        float half = Mathf.Max(0f, playerCapsule.height * 0.5f * Mathf.Abs(scale[dir]) - worldRadius);
        Vector3 axis = dir == 0 ? Vector3.right : dir == 1 ? Vector3.up : Vector3.forward;

        // Rigidbody pose, not the transform: with interpolation the transform lags behind
        Quaternion rotation = playerBody.rotation;
        Vector3 center = playerBody.position + rotation * Vector3.Scale(playerCapsule.center, scale);
        Vector3 halfSegment = rotation * axis * half;
        a = transform.InverseTransformPoint(center - halfSegment);
        b = transform.InverseTransformPoint(center + halfSegment);
        if (a.y > b.y) (a, b) = (b, a);
        radius = worldRadius / transform.lossyScale.x;
        return true;
    }

    // Cells whose balls could touch a capsule from a to b
    private bool CellRange(Vector3 a, Vector3 b, float reach, out int x0, out int x1, out int z0, out int z1)
    {
        Vector3 min = Vector3.Min(a, b) - Vector3.one * reach + offset;
        Vector3 max = Vector3.Max(a, b) + Vector3.one * reach + offset;
        x0 = Mathf.Max(0, Mathf.FloorToInt(min.x / CellSize));
        x1 = Mathf.Min(columns - 1, Mathf.CeilToInt(max.x / CellSize));
        z0 = Mathf.Max(0, Mathf.FloorToInt(min.z / CellSize));
        z1 = Mathf.Min(rows - 1, Mathf.CeilToInt(max.z / CellSize));
        return x0 <= x1 && z0 <= z1;
    }

    private Vector3 CellPosition(int x, int z) => new Vector3(x * CellSize, 0f, z * CellSize) - offset;

    private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float lengthSq = Vector3.Dot(ab, ab);
        if (lengthSq < 1e-8f) return a;
        return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSq);
    }
}
