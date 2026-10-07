using System.Collections.Generic;
using UnityEngine;

// One per wall/floor/ceiling. Spheres lie in this transform's local XZ plane and spring along transform.up.
// Each sphere has its own spring (SpringSphere); neighbors are also tied together with springs,
// so a pushed sphere drags the ones around it along, less the further away they are.
public class SpringGrid : MonoBehaviour
{
    public Vector3 Normal => transform.up;

    // Start runs after every SpringSphere.Awake has made its joint, while everything is still at rest
    void Start()
    {
        var spheres = GetComponentsInChildren<SpringSphere>();
        if (spheres.Length < 2) return;

        // Spacing = distance from the first sphere to its closest one; cells are counted from the first sphere
        Vector3 origin = transform.InverseTransformPoint(spheres[0].transform.position);
        float spacing = float.MaxValue;
        for (int i = 1; i < spheres.Length; i++)
        {
            Vector3 p = transform.InverseTransformPoint(spheres[i].transform.position);
            spacing = Mathf.Min(spacing, Vector3.Distance(p, origin));
        }

        var cells = new Dictionary<Vector2Int, SpringSphere>();
        foreach (var sphere in spheres)
        {
            Vector3 p = transform.InverseTransformPoint(sphere.transform.position) - origin;
            cells[new Vector2Int(Mathf.RoundToInt(p.x / spacing), Mathf.RoundToInt(p.z / spacing))] = sphere;
        }

        // Link each sphere to its right and forward neighbor, which covers every neighbor pair once
        foreach (var cell in cells)
        {
            if (cells.TryGetValue(cell.Key + Vector2Int.right, out var right)) cell.Value.LinkTo(right);
            if (cells.TryGetValue(cell.Key + Vector2Int.up, out var forward)) cell.Value.LinkTo(forward);
        }
    }
}
