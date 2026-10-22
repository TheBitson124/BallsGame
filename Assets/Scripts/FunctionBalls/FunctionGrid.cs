using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// A grid of balls with no physics at all: no Rigidbody, no collider, no GameObject per ball.
// Whatever comes near it bends it: any Rigidbody with an IFunctionGridBody (the player, a crate, a ball) and a capsule
// collider. Each body bends its own hole in a height function, the holes add up, and the balls follow its samples;
// this grid stands in for their colliders by keeping each body's velocity out of its hole.
// All balls are drawn in one instanced call with one material. A ball's color is its type: painting a color turns it
// into the type with that color, physics and all.
// Two-sided: whichever side of the grid the body is on is its top, so a tilted or upside-down grid works as a
// ceiling too. Jumping into it from below bends it up, landing on it from above bends it down.
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

    [Header("Bodies")]
    [Tooltip("Layers the balls react to. Any Rigidbody on them with an IFunctionGridBody that comes near the grid bends it")]
    public LayerMask bodyLayers = ~0;
    [Tooltip("How far past the balls (above, below and beyond the edges) a body is noticed, in local units. Further away, its hole goes back to rest and is dropped")]
    [Min(0f)] public float reach = 5f;
    [Tooltip("Gap to a ball under the body that still counts as standing on it, in local units")]
    [Min(0f)] public float touchDistance = 0.05f;
    [Tooltip("On: the balls give way from both sides, away from the body, so the grid also works as a ceiling or the underside of a slope. Off: only from the grid's own up side")]
    public bool twoSided = true;

    [Header("Debug")]
    [Tooltip("Logs to the console whenever this grid flattens its hole or resets the body's run of bounces, with the reason and what it had reached")]
    public bool logResets = true;

    // Share of an overlap pushed out per physics step. 1 = all at once, which overshoots into a small bounce
    private const float PushOut = 0.3f;
    private const int SolverIterations = 4;
    // Speed out of the surface (grid units/s) beyond what the grid itself gave the body that counts as a jump
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

    // Drawing
    private GraphicsBuffer ballBuffer;
    private GraphicsBuffer colorBuffer;
    private MaterialPropertyBlock drawProperties;
    private bool colorsChanged;
    private bool rebuild;
    private float deepest;            // deepest any ball's type goes, for the draw bounds
    private float highest;            // highest any ball's type rises above rest, for the draw bounds

    // The hole under the body, in local units: how deep it is right under them, and what it's doing.
    // Everything the grid keeps about one body. Every body near the grid gets its own, and keeps it until it's out
    // of reach and its hole has gone back to rest.
    private enum Phase { Settle, Dip, Bounce }
    private class Hole
    {
        public IFunctionGridBody body;
        public List<Collider> inside = new List<Collider>();   // its colliders in the grid's trigger
        public float press;               // how far the body presses into the balls (see StepHole)
        public Phase phase;
        public float depth;
        public float speed;               // into the surface; negative while it rises
        public float accel;               // into the surface; negative slows a dip, positive speeds up a bounce
        public float dipTarget;
        public float launchSpeed;
        public bool touching;             // against the balls' face on the body's side at the start of the last step
        public bool standing;             // touching from above in the world, so the controller counts it as ground
        public float side = 1f;           // which side of the grid the body is on: 1 = local +y, -1 = local -y
        public Vector3 center;            // grid space; follows the body, stays put while waiting for the reset
        public bool waitingForReset;      // the body left: the hole goes back to rest where it was before resetting
        public bool wasTouching;
        public bool carriedLastStep;      // last step set the body's speed out of the surface (dip or launch)
        public float rideSpeed;           // how fast the balls the body stood on rose last step, grid units/s
        public float lastSetSpeed;        // vertical speed this grid left the body with last step, grid units/s
        public float fallSpeed;           // speed into the surface during the last step in the air

        // Run of bounces: starts with the first landing on a type and lasts until the body leaves the grid.
        // Its bounce starts at the type's and fades to 1 over the type's maxBounceCount bounces.
        public FunctionSphereType runType;    // type the run is on; null = no run
        public float runBounce;               // bounce for the next landing of the run
        public float runLaunch;               // last launch speed of the run, grid units/s
        public bool runLastCounted;           // the last landing was fast enough to count toward maxBounceCount

        // Still there: a destroyed component only compares equal to null as a UnityEngine.Object
        public bool Alive => body is Object o ? o != null : body != null;
        public string Name => body is Object o && o ? o.name : "body";

        // Any of its colliders still in the trigger. Unity sends no OnTriggerExit for a collider that's destroyed
        // or switched off, so those are dropped here
        public bool InRange()
        {
            for (int i = inside.Count - 1; i >= 0; i--)
            {
                Collider c = inside[i];
                if (!c || !c.enabled || !c.gameObject.activeInHierarchy) inside.RemoveAt(i);
            }
            return inside.Count > 0;
        }

        // Grid space to and from the body's side: y flipped on the underside. Its own inverse
        public Vector3 Flip(Vector3 p) => new Vector3(p.x, p.y * side, p.z);
    }
    private readonly List<Hole> holes = new List<Hole>();

    // Waking: a body entering the trigger wakes the grid; with no holes left and every ball at rest it sleeps
    // and costs nothing per step
    private BoxCollider trigger;
    private bool triggerChanged;
    private bool asleep = true;
    private float widest;             // widest any ball's type's hole is, for the area the holes bend
    // Balls the holes bent last step, in cells; empty when bentX0 > bentX1
    private int bentX0 = 0, bentX1 = -1, bentZ0 = 0, bentZ1 = -1;
    private bool moving;              // some ball moved last step
    private bool drawnStill;          // drawn where they stopped since; nothing to upload until they move again

    private int Count => types != null ? types.Length : 0;
    private Mesh Mesh => ballMesh ? ballMesh : Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

    // Changes one ball's type at runtime, e.g. a mud patch inside solid ground. It takes the type's color with it
    public void SetType(int x, int z, FunctionSphereType type)
    {
        int i = x + z * columns;
        types[i] = type;
        if (type.DeepestDepth > deepest || type.HighestRise > highest || type.holeWidth > widest) triggerChanged = true;
        deepest = Mathf.Max(deepest, type.DeepestDepth);
        highest = Mathf.Max(highest, type.HighestRise);
        widest = Mathf.Max(widest, type.holeWidth);
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
        triggerChanged = true;   // not resized here: Unity doesn't allow it in OnValidate
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

        trigger = GetComponent<BoxCollider>();
        if (!trigger) trigger = gameObject.AddComponent<BoxCollider>();
        UpdateTrigger();
    }

    // The trigger bodies wake the grid through: the balls, as deep and as high as they go, plus reach all round
    private void UpdateTrigger()
    {
        triggerChanged = false;
        if (!trigger) return;
        float r = sphereSize * 0.5f;
        trigger.isTrigger = true;
        trigger.center = Vector3.zero;
        trigger.size = 2f * new Vector3(offset.x + r + reach, Mathf.Max(deepest, highest) + r + reach, offset.z + r + reach);
    }

    // A body coming into reach wakes the grid and gets a hole
    void OnTriggerEnter(Collider other)
    {
        IFunctionGridBody body = BodyOf(other);
        if (body == null) return;
        Hole hole = HoleOf(body);
        if (hole == null)
        {
            hole = new Hole { body = body };
            holes.Add(hole);
        }
        if (!hole.inside.Contains(other)) hole.inside.Add(other);
        asleep = false;
    }

    void OnTriggerExit(Collider other)
    {
        IFunctionGridBody body = BodyOf(other);
        if (body != null) HoleOf(body)?.inside.Remove(other);
    }

    private IFunctionGridBody BodyOf(Collider other)
    {
        if ((bodyLayers.value & (1 << other.gameObject.layer)) == 0) return null;
        Rigidbody rb = other.attachedRigidbody;
        return rb ? rb.GetComponent<IFunctionGridBody>() : null;
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
        // Bodies still in the trigger get a fresh hole: they won't enter it again to get one
        for (int k = 0; k < holes.Count; k++)
        {
            EndRun(holes[k], "grid rebuilt");
            holes[k] = new Hole { body = holes[k].body, inside = holes[k].inside };
        }
        bentX0 = bentZ0 = 0;
        bentX1 = bentZ1 = -1;
        asleep = holes.Count == 0;
        moving = false;
        drawnStill = true;   // uploaded at rest below
        deepest = sphereType ? sphereType.DeepestDepth : 0f;
        highest = sphereType ? sphereType.HighestRise : 0f;
        widest = sphereType ? sphereType.holeWidth : 0f;

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
                    widest = Mathf.Max(widest, types[i].holeWidth);
                }
                UpdateColor(i);
            }
        }
        triggerChanged = true;
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
        if (triggerChanged) UpdateTrigger();
        if (asleep) return;
        System.Array.Copy(height, previousHeight, height.Length);

        for (int k = holes.Count - 1; k >= 0; k--)
            if (!StepHole(holes[k])) holes.RemoveAt(k);
        UpdateHeights();

        // Nothing left on it and every ball back at rest: show them at rest and sleep until a body comes back
        if (holes.Count == 0)
        {
            System.Array.Copy(height, previousHeight, height.Length);
            UploadBalls(1f);
            asleep = true;
        }
    }

    // Every hole bends the balls. Holes on the same side add up where they overlap, as deep as the ball's type
    // goes and no deeper; holes from opposite sides push against each other.
    // Only balls a hole reaches are worked out, plus last step's (to put them back at rest); the rest stay at rest.
    private void UpdateHeights()
    {
        int x0 = 0, x1 = -1, z0 = 0, z1 = -1;
        float half = widest * 0.5f;
        foreach (Hole h in holes)
        {
            if (h.depth == 0f) continue;
            int hx0 = Mathf.Max(0, Mathf.FloorToInt((h.center.x - half + offset.x) / CellSize));
            int hx1 = Mathf.Min(columns - 1, Mathf.CeilToInt((h.center.x + half + offset.x) / CellSize));
            int hz0 = Mathf.Max(0, Mathf.FloorToInt((h.center.z - half + offset.z) / CellSize));
            int hz1 = Mathf.Min(rows - 1, Mathf.CeilToInt((h.center.z + half + offset.z) / CellSize));
            if (hx0 > hx1 || hz0 > hz1) continue;
            if (x0 > x1) (x0, x1, z0, z1) = (hx0, hx1, hz0, hz1);
            else (x0, x1, z0, z1) = (Mathf.Min(x0, hx0), Mathf.Max(x1, hx1), Mathf.Min(z0, hz0), Mathf.Max(z1, hz1));
        }

        int ux0 = x0, ux1 = x1, uz0 = z0, uz1 = z1;
        if (bentX0 <= bentX1)
        {
            if (ux0 > ux1) (ux0, ux1, uz0, uz1) = (bentX0, bentX1, bentZ0, bentZ1);
            else (ux0, ux1, uz0, uz1) = (Mathf.Min(ux0, bentX0), Mathf.Max(ux1, bentX1), Mathf.Min(uz0, bentZ0), Mathf.Max(uz1, bentZ1));
        }
        (bentX0, bentX1, bentZ0, bentZ1) = (x0, x1, z0, z1);

        moving = false;
        for (int z = uz0; z <= uz1; z++)
        {
            for (int x = ux0; x <= ux1; x++)
            {
                int i = x + z * columns;
                float top = 0f, bottom = 0f;
                foreach (Hole h in holes)
                {
                    if (h.depth == 0f) continue;
                    float depth = Mathf.Min(HoleDepth(i, h.center, h.depth), h.press);
                    if (h.side > 0f) top += depth;
                    else bottom += depth;
                }
                height[i] = types[i].ClampDepth(bottom) - types[i].ClampDepth(top);
                if (height[i] != previousHeight[i]) moving = true;
            }
        }
    }

    // Found by hand, not List.Find: a lambda would allocate every step
    private Hole HoleOf(IFunctionGridBody body)
    {
        foreach (Hole h in holes)
            if (h.body == body) return h;
        return null;
    }

    // One body's step: its hole follows it, dips and bounces, and the balls hold it up.
    // A body that's gone (out of reach, destroyed, its capsule off) leaves its hole where it was to go back to rest.
    // Returns false once there's nothing left of the hole, so it can be dropped.
    private bool StepHole(Hole h)
    {
        if (!h.InRange() || !GetCapsule(h, out Vector3 a, out Vector3 b, out float radius))
        {
            h.touching = h.standing = h.carriedLastStep = false;
            h.rideSpeed = 0f;
            h.lastSetSpeed = 0f;
            h.press = float.MaxValue;
            if (!h.waitingForReset)
            {
                if (h.depth == 0f && h.phase == Phase.Settle)
                {
                    EndRun(h, "out of reach");
                    return false;
                }
                h.waitingForReset = true;
                if (logResets)
                    Debug.Log($"[{name}/{h.Name}] out of reach: waiting for the balls to go back to rest ({h.depth:0.###} deep)", this);
            }
            SettleBack(h);
            return h.waitingForReset;
        }

        // From here on y is out of the grid on the body's side, so the hole always goes away from the body
        UpdateSide(h, (a + b) * 0.5f);
        a = h.Flip(a);
        b = h.Flip(b);
        if (a.y > b.y) (a, b) = (b, a);

        // Leaving the grid starts the wait for its reset: the hole stays where the body left it and goes back to
        // rest there (see SettleBack). Already at rest, there's nothing to wait for.
        if (!h.waitingForReset && !OverGrid(a))
        {
            if (h.depth != 0f || h.phase != Phase.Settle)
            {
                h.waitingForReset = true;
                if (logResets)
                    Debug.Log($"[{name}/{h.Name}] left the grid: waiting for the balls to go back to rest ({h.depth:0.###} deep)", this);
            }
            else EndRun(h, "body left the grid");
        }

        float oldDepth = h.depth;
        FunctionSphereType ground = GroundType(a);
        bool carry = false;
        float carrySpeed = 0f;
        if (h.waitingForReset) SettleBack(h);
        else
        {
            h.center = a;
            float arrival = Arrival(h, a, radius, ground, out float arrivalSpeed);
            carry = UpdateHole(h, ground, OverGrid(a), arrival, arrivalSpeed, out carrySpeed);
        }

        // Balls only give way as far as the body presses in: the body's lowest point below the tops of
        // balls at rest. In the air, or standing on something above the grid, nothing presses and the grid is flat.
        // A hole left behind while waiting for the reset isn't the body's any more, so it shows in full.
        h.press = h.waitingForReset ? float.MaxValue : Mathf.Max(0f, sphereSize * 0.5f - (a.y - radius));

        SolveContacts(h, a, b, radius, GroundType(h.center), oldDepth, carry, carrySpeed);
        if (h.standing) h.body.ReportSlowdown(ground.slowdown);
        return true;
    }

    // The body's side is the one their capsule's center is on, measured against the balls as they're bent now,
    // not the flat grid: sinking deep into a hole takes the body below the flat grid without going through it.
    // Only clearly past the balls counts as the other side. Going round flattens the hole at once: a hole bent away
    // from the old side would bend toward the new one.
    // center: the capsule's center in grid space, not flipped
    private void UpdateSide(Hole h, Vector3 center)
    {
        if (!twoSided && h.side > 0f) return;
        Vector3 c = h.Flip(center);
        float sheet = OverGrid(c)
            ? -GroundType(c).Falloff(c.x - h.center.x, c.z - h.center.z) * GroundType(c).ClampDepth(h.depth)
            : 0f;
        if (twoSided && c.y >= sheet - sphereSize * 0.5f) return;
        h.side = -h.side;
        if (logResets && (h.depth != 0f || h.phase != Phase.Settle))
            Debug.Log($"[{name}/{h.Name}] went round to the grid's {(h.side > 0f ? "top" : "underside")}", this);
        ResetHole(h, "body went round to the other side");
        EndRun(h, "body went round to the other side");
        h.waitingForReset = false;
        h.touching = h.standing = h.wasTouching = h.carriedLastStep = false;
        h.rideSpeed = 0f;
        h.lastSetSpeed = -h.lastSetSpeed;
    }


    // Body's velocity by the end of this step if this grid did nothing (gravity included), grid units/s on the
    // body's side
    private Vector3 GridVelocity(Hole h, out Vector3 worldVelocity)
    {
        Rigidbody rb = h.body.Body;
        worldVelocity = rb.velocity + (rb.useGravity ? Physics.gravity * Time.fixedDeltaTime : Vector3.zero);
        return h.Flip(transform.InverseTransformDirection(worldVelocity) / transform.lossyScale.x);
    }

    // Whether a body in the air reaches the surface right under them during this step: the share of the step
    // they're still in the air (0 to 1), or -1 if they don't arrive. arrivalSpeed is their speed into the surface.
    // Lets a landing start the dip in the step the body arrives, so a fast landing sinks straight in instead of
    // being stopped dead on the surface for a step and then pulled down again.
    private float Arrival(Hole h, Vector3 a, float radius, FunctionSphereType ground, out float arrivalSpeed)
    {
        arrivalSpeed = -GridVelocity(h, out _).y;
        if (h.touching || arrivalSpeed <= 0f || !OverGrid(a)) return -1f;
        float surface = sphereSize * 0.5f - ground.ClampDepth(h.depth);
        float gap = a.y - radius - surface;
        float travel = arrivalSpeed * Time.fixedDeltaTime;
        if (gap > travel) return -1f;
        return Mathf.Max(0f, gap) / travel;
    }

    // Moves the hole one step. A landing dips it, then it comes back up to restDepth: slowly, or for a bouncy type
    // faster and faster so it throws the body out at the end.
    // arrival: share of this step before the body reaches the surface, -1 if they don't (see Arrival)
    // Returns true to set the body's speed out of the surface this step: pulled down with the dip (the contacts
    // hold them back to the speed of the balls they actually rest on, which sink slower off-center), and launched
    // at the top of a bounce.
    private bool UpdateHole(Hole h, FunctionSphereType ground, bool overGrid, float arrival, float arrivalSpeed, out float carrySpeed)
    {
        float dt = Time.fixedDeltaTime;
        float dipTime = dt;   // a dip starting this step only runs once the body arrives
        carrySpeed = 0f;
        bool arriving = arrival >= 0f;
        bool onIt = h.touching || arriving;
        bool landed = onIt && !h.wasTouching;
        h.wasTouching = onIt;

        // Jumped or walked off mid-dip: the hole just settles, and the body keeps whatever speed they left with
        if (!onIt) h.phase = Phase.Settle;

        if (arriving) h.fallSpeed = arrivalSpeed;
        if (landed && h.fallSpeed > 0f && h.fallSpeed >= ground.minLandingSpeed)
        {
            float target = Mathf.Lerp(ground.restDepth, ground.landingDepth, h.fallSpeed / ground.fullLandingSpeed);
            if (target > h.depth + 1e-4f)
            {
                // Slows the body from their landing speed to a stop exactly at the bottom of the dip
                h.phase = Phase.Dip;
                h.dipTarget = target;
                h.speed = h.fallSpeed;
                h.accel = -h.fallSpeed * h.fallSpeed / (2f * (target - h.depth));
                h.launchSpeed = BounceLaunch(h, ground, h.fallSpeed);
                if (arriving) dipTime = dt * (1f - arrival);
            }
        }

        switch (h.phase)
        {
            case Phase.Dip:
                h.speed += h.accel * dipTime;
                h.depth += h.speed * dipTime;
                if (h.speed > 0f && h.depth < h.dipTarget)
                {
                    carrySpeed = -h.speed;
                    return true;
                }
                h.depth = h.dipTarget;
                h.speed = 0f;
                // Speeds up from a stop at the bottom to launchSpeed exactly at restDepth
                if (h.launchSpeed > 0f && h.depth > ground.restDepth)
                {
                    h.phase = Phase.Bounce;
                    h.accel = -h.launchSpeed * h.launchSpeed / (2f * (h.depth - ground.restDepth));
                }
                else h.phase = Phase.Settle;
                return true;

            case Phase.Bounce:
                // The rising balls push the body up through the contacts
                h.speed += h.accel * dt;
                h.depth += h.speed * dt;
                if (h.depth > ground.restDepth) return false;
                // Back at the top: the hole stops and the body flies on
                h.depth = ground.restDepth;
                h.speed = 0f;
                h.phase = Phase.Settle;
                carrySpeed = h.launchSpeed;
                return true;

            default:
                // In the air above a type that holds its shape (a raised platform jumped on): stay put
                if (!onIt && overGrid && ground.holdWhileAirborne) return false;
                // Nothing on it (in the air above it, too): back to flat.
                // Moving away from flat (sinking in, or rising into a mound for a negative restDepth) goes at
                // sinkSpeed; moving back toward flat or restDepth at riseSpeed
                float rest = onIt ? ground.restDepth : 0f;
                bool awayFromFlat = rest != 0f && Mathf.Sign(rest - h.depth) == Mathf.Sign(rest);
                float speed = awayFromFlat ? ground.sinkSpeed : ground.riseSpeed;
                h.depth = Mathf.MoveTowards(h.depth, rest, speed * dt);
                return false;
        }
    }

    // Launch speed for a landing. A run of bounces starts at the type's bounce, and every counted bounce cuts
    // what's left above 1 until it reaches 1 after maxBounceCount: bouncing climbs quickly at first, then holds.
    // Only landings at minBounceSpeed or faster count. Slower ones bounce at the current value without using it up,
    // so a run that starts with small hops isn't spent before it gets going.
    // A counted landing right after a counted launch is measured against that launch, so jumping on each bounce
    // can't keep pushing the body higher. Anything else counts in full: the run's first landing (coming in from
    // higher ground sets a higher height to hold) and the first landing fast enough to count after small hops.
    private float BounceLaunch(Hole h, FunctionSphereType ground, float landingSpeed)
    {
        if (h.runType != ground)
        {
            if (h.runType) EndRun(h, $"landed on {ground.name}");
            h.runType = ground;
            h.runBounce = ground.bounce;
            h.runLastCounted = false;
        }

        bool counts = landingSpeed >= ground.minBounceSpeed;
        if (counts && h.runLastCounted) landingSpeed = Mathf.Min(landingSpeed, h.runLaunch);
        h.runLaunch = landingSpeed * h.runBounce;
        if (counts) h.runBounce = ground.NextBounce(h.runBounce);
        h.runLastCounted = counts;
        return h.runLaunch;
    }

    // Waiting for the reset after the body left: the hole goes back to rest where it was left, at its type's
    // riseSpeed, and nothing the body does changes it. Once it's flat every ball is back at rest, and the grid
    // resets: the run of bounces ends and the hole follows the body again, starting flat (so a raised platform
    // only grows once the body stands on it again). Ball types and colors are never touched.
    private void SettleBack(Hole h)
    {
        h.wasTouching = h.touching;   // no landing is missed or invented when the hole follows the body again
        h.phase = Phase.Settle;
        h.speed = 0f;
        h.depth = Mathf.MoveTowards(h.depth, 0f, GroundType(h.center).riseSpeed * Time.fixedDeltaTime);
        if (h.depth != 0f) return;

        h.waitingForReset = false;
        if (logResets) Debug.Log($"[{name}/{h.Name}] all its balls back at rest: hole reset", this);
        EndRun(h, "body left the grid");
    }

    // Flattens the hole under the body at once and drops any dip or bounce in progress
    private void ResetHole(Hole h, string reason)
    {
        if (h.depth == 0f && h.phase == Phase.Settle) return;
        if (logResets)
            Debug.Log($"[{name}/{h.Name}] hole reset to flat: {reason}. It was {h.depth:0.###} deep ({h.phase})", this);
        h.depth = 0f;
        h.speed = 0f;
        h.phase = Phase.Settle;
    }

    // Ends the run of bounces, so the next landing starts again from its type's full bounce
    private void EndRun(Hole h, string reason)
    {
        if (!h.runType) return;
        if (logResets)
            Debug.Log($"[{name}/{h.Name}] bounce run on {h.runType.name} reset: {reason}. Bounce had reached {h.runBounce:0.###} (starts at {h.runType.bounce:0.###}), last launch {h.runLaunch:0.##} units/s", this);
        h.runType = null;
    }

    private float HoleDepth(int i, Vector3 center, float depth)
    {
        Vector3 p = CellPosition(i % columns, i / columns);
        FunctionSphereType type = types[i];
        return type.Falloff(p.x - center.x, p.z - center.z) * type.ClampDepth(depth);
    }

    // Whether a point is above the grid's balls (not past its edge), in grid space
    private bool OverGrid(Vector3 p) =>
        Mathf.Abs(p.x) <= offset.x + CellSize * 0.5f && Mathf.Abs(p.z) <= offset.z + CellSize * 0.5f;

    // Type of the ball under the body's center; off the grid, the nearest edge ball's
    private FunctionSphereType GroundType(Vector3 p)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt((p.x + offset.x) / CellSize), 0, columns - 1);
        int z = Mathf.Clamp(Mathf.RoundToInt((p.z + offset.z) / CellSize), 0, rows - 1);
        return types[x + z * columns];
    }

    // Balls move in physics steps; draw them between the last two, like Rigidbody interpolation does for the body
    void Update()
    {
        if (rebuild) Build();
        if (ballBuffer == null) return;
        if (!Application.isPlaying || asleep) return;
        // Still balls only need drawing once more, to land exactly on where they stopped
        if (moving) drawnStill = false;
        else if (drawnStill) return;
        else drawnStill = true;
        UploadBalls(Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime));
    }

    // Contacts are against the hole, not the drawn heights: the balls give way freely until the hole
    // is as deep as it is right now, and only there hold the body up.
    // Works on velocity like PhysX's own solver, with each ball moving from its place in oldDepth's hole to the
    // current one this step: speed that would carry the body into a ball by the end of this step is removed,
    // and an existing overlap is pushed out a share per step on top of following the ball.
    // Balls the body could only reach this step count too, so a fast fall can't pass through between steps.
    // On top of the real balls, the function's own peak right under the body holds them up like one more ball,
    // so they stand at exactly restDepth (−100 = 100 up) wherever they are between ball centers. That only matters
    // for a mound, where every real ball is off the peak and lower; in a hole the balls around are higher anyway.
    // carry: the body rides the surface this step, moving out of it at carrySpeed (landing dip and bounce)
    private void SolveContacts(Hole h, Vector3 a, Vector3 b, float radius, FunctionSphereType ground, float oldDepth, bool carry, float carrySpeed)
    {
        float scale = transform.lossyScale.x;
        float dt = Time.fixedDeltaTime;
        // Gravity is added after FixedUpdate; include it so a body at rest stays exactly at rest
        Vector3 v = GridVelocity(h, out Vector3 worldVelocity);
        float speedIn = -v.y;
        bool overGrid = OverGrid(a);
        float peakFrom = -ground.ClampDepth(oldDepth);
        float peakSpeed = (-ground.ClampDepth(h.depth) - peakFrom) / dt;

        // A jump the controller added last step is in the velocity now: compared to the speed this grid left the
        // body with last step (plus gravity since), they move out of the surface clearly faster. Then let them go
        // instead of pulling them back into the dip (or, at the top of a bounce, they already go faster than the
        // launch). Not compared to the carry itself: a landing that wasn't caught on arrival (a ball off to the side
        // came first) is stopped by the contacts a step before the dip starts pulling, and that difference isn't a jump.
        float gravityStep = h.body.Body.useGravity ? h.side * transform.InverseTransformDirection(Physics.gravity * dt).y / scale : 0f;
        bool jumped = v.y > h.lastSetSpeed + gravityStep + JumpSpeed;
        if (carry && jumped)
        {
            carry = false;
            if (h.phase == Phase.Dip) h.phase = Phase.Settle;
        }
        // Velocity change, so a jump the controller added this step still goes on top
        else if (carry) v.y = carrySpeed;
        // Riding the surface: the balls under the body rose at rideSpeed last step and carried them along.
        // Take back whatever part of it the ground no longer moves at, so when a rising mound (or a bounce settling
        // back) stops, they stop with it instead of flying on. Only that ground speed is taken back: a jump is on
        // top of it in the velocity and stays. Launches (carry) aren't taken back either.
        else if (h.touching && !h.carriedLastStep && overGrid) v.y -= Mathf.Max(0f, h.rideSpeed - Mathf.Max(0f, peakSpeed));
        h.carriedLastStep = carry;

        float reach = radius + sphereSize * 0.5f;
        float margin = v.magnitude * dt + touchDistance;
        if (!CellRange(a, b, reach + margin, out int x0, out int x1, out int z0, out int z1))
        {
            h.touching = h.standing = false;
            h.fallSpeed = speedIn;
            h.rideSpeed = 0f;
            h.lastSetSpeed = v.y;
            return;
        }

        bool contact = false;      // against the face on the body's side: a floor, slope or ceiling
        bool grounded = false;     // and that face holds the body up in the world
        float supportSpeed = 0f;   // how fast the balls the body stands on rise this step
        bool lastIteration = false;
        for (int iteration = 0; iteration < SolverIterations; iteration++)
        {
            lastIteration = iteration == SolverIterations - 1;
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = x + z * columns;
                    Vector3 s = CellPosition(x, z);
                    s.y = -HoleDepth(i, h.center, oldDepth);
                    Contact(s, (-HoleDepth(i, h.center, h.depth) - s.y) / dt);
                }
            }
            if (OverGrid(h.center)) Contact(new Vector3(h.center.x, peakFrom, h.center.z), peakSpeed);
        }

        // One ball at s, moving up at ballSpeed this step
        void Contact(Vector3 s, float ballSpeed)
        {
            Vector3 d = ClosestOnSegment(a, b, s) - s;
            float dist = d.magnitude;
            float gap = dist - reach;
            if (gap > margin) return;

            Vector3 n = dist > 1e-5f ? d / dist : Vector3.up;

            // Close the gap no faster than it ends this step; inside a ball, move out instead
            float minSpeed = (gap > 0f ? -gap / dt : -gap * PushOut / dt) + ballSpeed * n.y;
            float speed = Vector3.Dot(v, n);
            if (speed < minSpeed)
            {
                v += n * (minSpeed - speed);
                speed = minSpeed;
            }

            // Touching: against it now, or reaching it by the end of this step. Arriving counts, so a landing
            // the hole already gives way to this step (see Arrival) isn't dropped for being a step early.
            // Only once the velocity is final, in the last pass
            if (!lastIteration || n.y <= 0.5f) return;
            float endGap = gap + (speed - ballSpeed * n.y) * dt;
            if (gap > touchDistance && endGap > touchDistance) return;
            contact = true;
            supportSpeed = Mathf.Max(supportSpeed, ballSpeed);
            if (transform.TransformDirection(h.Flip(n)).y > 0.5f) grounded = true;
        }

        // The ground's own speed, not the body's: a jump is in the body's velocity from the next step on
        // and must not count as speed the balls gave them
        h.rideSpeed = supportSpeed;
        h.lastSetSpeed = v.y;   // before a jump this step: that shows up in the velocity next step
        h.body.Body.AddForce(transform.TransformDirection(h.Flip(v)) * scale - worldVelocity, ForceMode.VelocityChange);
        h.body.ReportGround(grounded);

        if (!h.touching) h.fallSpeed = speedIn;   // came in from the air: the speed a landing starts from
        h.touching = contact;
        h.standing = grounded;
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

    // Everything the balls can reach: the grid, from the deepest hole up to the tops of balls on the highest mound.
    // Two-sided, the balls can bend either way
    private Bounds WorldBounds()
    {
        float r = sphereSize * 0.5f;
        float down = twoSided ? Mathf.Max(deepest, highest) : deepest;
        float up = twoSided ? down : highest;
        Vector3 min = new Vector3(-offset.x - r, -down - r, -offset.z - r);
        Vector3 max = new Vector3(offset.x + r, up + r, offset.z + r);
        var bounds = new Bounds(transform.TransformPoint(min), Vector3.zero);
        for (int corner = 1; corner < 8; corner++)
        {
            Vector3 p = new Vector3((corner & 1) != 0 ? max.x : min.x, (corner & 2) != 0 ? max.y : min.y, (corner & 4) != 0 ? max.z : min.z);
            bounds.Encapsulate(transform.TransformPoint(p));
        }
        return bounds;
    }

    // Centers of the body capsule's two hemispheres and its radius, in grid space
    private bool GetCapsule(Hole h, out Vector3 a, out Vector3 b, out float radius)
    {
        a = b = Vector3.zero;
        radius = 0f;
        if (!h.Alive) return false;
        Rigidbody rb = h.body.Body;
        CapsuleCollider capsule = h.body.Capsule;
        if (!rb || !capsule || !capsule.enabled) return false;

        int dir = capsule.direction;
        Vector3 scale = capsule.transform.lossyScale;
        float radiusScale = Mathf.Max(Mathf.Abs(scale[(dir + 1) % 3]), Mathf.Abs(scale[(dir + 2) % 3]));
        float worldRadius = capsule.radius * radiusScale;
        float half = Mathf.Max(0f, capsule.height * 0.5f * Mathf.Abs(scale[dir]) - worldRadius);
        Vector3 axis = dir == 0 ? Vector3.right : dir == 1 ? Vector3.up : Vector3.forward;

        // Rigidbody pose, not the transform: with interpolation the transform lags behind
        Quaternion rotation = rb.rotation;
        Vector3 center = rb.position + rotation * Vector3.Scale(capsule.center, scale);
        Vector3 halfSegment = rotation * axis * half;
        a = transform.InverseTransformPoint(center - halfSegment);
        b = transform.InverseTransformPoint(center + halfSegment);
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
