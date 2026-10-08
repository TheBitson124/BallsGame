using UnityEngine;

public class GridSpawner : MonoBehaviour
{
    public GameObject spherePrefab;
    public int rows = 40;
    public int columns = 40;
    [Tooltip("Sphere diameter, in this object's local units. The prefab is 1 unit wide at scale 1")]
    [Min(0.001f)] public float sphereSize = 1f;
    [Tooltip("Distance between sphere centers, in sphere diameters. 1 = touching, no gaps or overlaps")]
    [Min(0.001f)] public float spacing = 1f;

    public SphereTypeScriptableObject sphereType;

    // Distance between neighboring sphere centers, in local units
    public float CellSize => spacing * sphereSize;
    // Sphere diameter in world units; sphere type lengths are multiplied by this
    public float WorldSphereSize => sphereSize * transform.lossyScale.x;

    [ContextMenu("Generate Grid")]
    public void GenerateGrid()
    {
        ClearGrid();

        Vector3 offset = new Vector3((columns - 1) * CellSize * 0.5f, 0, (rows - 1) * CellSize * 0.5f);

        for (int x = 0; x < columns; x++)
        {
            for (int z = 0; z < rows; z++)
            {
                Vector3 spawnPos = new Vector3(x * CellSize, 0, z * CellSize) - offset;
                GameObject obj = Instantiate(spherePrefab, transform);
                obj.transform.localPosition = spawnPos;
                obj.transform.localScale = Vector3.one * sphereSize;
                obj.name = $"Sphere_{x}_{z}";
                obj.GetComponent<SpringSphere>().SetType(sphereType);
            }
        }
    }

    [ContextMenu("Clear Grid")]
    public void ClearGrid()
    {
        while (transform.childCount > 0)
        {
            DestroyImmediate(transform.GetChild(0).gameObject);
        }
    }
}
