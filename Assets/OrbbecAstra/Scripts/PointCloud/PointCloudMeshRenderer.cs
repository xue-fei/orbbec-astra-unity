using UnityEngine;

/*
 * Real point-cloud renderer for the Orbbec Astra in Unity (Built-in pipeline).
 *
 * Pipeline:
 *   DepthToTexture._depthBuffer (float[] mm)  +  ColourToTexture._colorMapBuffer (RGB float[])
 *        --> DepthToPoints.compute  -->  pointsPos / pointsCol  (StructuredBuffers)
 *        --> DrawMeshInstancedIndirect (one instanced quad per point)  -->  real mesh geometry
 *
 * Because every point is a real (instanced) mesh quad it participates in depth/occlusion
 * and can be read / exported like any other geometry.
 *
 * Usage:
 *   1. Put DepthToTexture + ColourToTexture in the scene (already there).
 *   2. Add this script to an empty GameObject.
 *   3. Assign pointCloudCompute (DepthToPoints.compute). Material is auto-created if left empty.
 *   4. Tune fx/fy/cx/cy, pointSize, flipY on-device until the cloud looks right.
 *
 * Dispatched in LateUpdate so the depth/colour buffers (filled during AstraController.Update)
 * are guaranteed fresh.
 */

public class PointCloudMeshRenderer : MonoBehaviour
{
    [Header("References")]
    public ComputeShader pointCloudCompute;
    public Material pointMaterial;          // optional: auto-created from PointCloud/PointCloud shader
    public DepthToTexture depthToTexture;   // required (source of depth buffer)
    public ColourToTexture colourToTexture; // optional (source of colour buffer)

    [Header("Camera Intrinsics (tune on-device)")]
    public float fx = 570.34f;
    public float fy = 570.34f;
    public float cx = 320f;
    public float cy = 240f;
    public float scale = 0.01f;             // mm -> m
    public int   cutOff = 10000;            // mm, discard points farther than this

    [Header("Rendering")]
    public float pointSize = 0.005f;        // NDC fraction -> constant on-screen size
    public bool  flipY = true;              // match Astra raw row order
    public bool  mirrorX = false;

    private ComputeBuffer _pointsPos;
    private ComputeBuffer _pointsCol;
    private GraphicsBuffer _argsBuffer;
    private Mesh _quad;
    private Material _mat;
    private int _kernel;
    private int _width, _height;

    private void Start()
    {
        _width = AstraConstants.Width;
        _height = AstraConstants.Height;

        if (depthToTexture == null)
            depthToTexture = FindObjectOfType<DepthToTexture>();
        if (colourToTexture == null)
            colourToTexture = FindObjectOfType<ColourToTexture>();

        if (depthToTexture == null)
        {
            Debug.LogError("[PointCloud] DepthToTexture not found in scene. Point cloud disabled.");
            enabled = false;
            return;
        }

        if (pointCloudCompute == null)
        {
            foreach (var cs in Resources.FindObjectsOfTypeAll<ComputeShader>())
            {
                if (cs.name == "DepthToPoints") { pointCloudCompute = cs; break; }
            }
        }
        if (pointCloudCompute == null)
        {
            Debug.LogError("[PointCloud] DepthToPoints.compute not found. Assign pointCloudCompute in the inspector.");
            enabled = false;
            return;
        }

        int count = _width * _height;
        _pointsPos = new ComputeBuffer(count, 12); // float3
        _pointsCol = new ComputeBuffer(count, 12); // float3

        _quad = BuildQuadMesh();

        _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, sizeof(int));
        _argsBuffer.SetData(new int[] { 6, count, 0, 0, 0 });

        if (pointMaterial == null)
        {
            var shader = Shader.Find("PointCloud/PointCloud");
            if (shader == null)
            {
                Debug.LogError("[PointCloud] Shader 'PointCloud/PointCloud' not found.");
                enabled = false;
                return;
            }
            _mat = new Material(shader);
            pointMaterial = _mat;
        }
        else
        {
            _mat = pointMaterial;
        }

        _kernel = pointCloudCompute.FindKernel("DepthToPoints");
    }

    private static Mesh BuildQuadMesh()
    {
        var mesh = new Mesh();
        // corner offset stored in POSITION, range [-0.5, 0.5]
        var verts = new Vector3[4]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f),
        };
        var tris = new int[] { 0, 1, 2, 0, 2, 3 };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        return mesh;
    }

    private void LateUpdate()
    {
        var depthBuffer = depthToTexture.DepthBuffer;
        if (depthBuffer == null) return;

        var colorBuffer = (colourToTexture != null) ? colourToTexture.ColorBuffer : null;

        pointCloudCompute.SetBuffer(_kernel, "DepthBuffer", depthBuffer);
        if (colorBuffer != null)
            pointCloudCompute.SetBuffer(_kernel, "ColorBuffer", colorBuffer);
        pointCloudCompute.SetBuffer(_kernel, "PointsPos", _pointsPos);
        pointCloudCompute.SetBuffer(_kernel, "PointsCol", _pointsCol);
        pointCloudCompute.SetInt("width", _width);
        pointCloudCompute.SetInt("height", _height);
        pointCloudCompute.SetFloat("cutOff", cutOff);
        pointCloudCompute.SetFloat("scale", scale);
        pointCloudCompute.SetFloat("fx", fx);
        pointCloudCompute.SetFloat("fy", fy);
        pointCloudCompute.SetFloat("cx", cx);
        pointCloudCompute.SetFloat("cy", cy);
        pointCloudCompute.SetInt("_mirrorX", mirrorX ? 1 : 0);
        pointCloudCompute.SetInt("_flipY", flipY ? 1 : 0);
        pointCloudCompute.SetInt("_hasColor", colorBuffer != null ? 1 : 0);

        int groupsX = Mathf.CeilToInt((float)_width / 8f);
        int groupsY = Mathf.CeilToInt((float)_height / 8f);
        pointCloudCompute.Dispatch(_kernel, groupsX, groupsY, 1);

        _mat.SetBuffer("_PointsPos", _pointsPos);
        _mat.SetBuffer("_PointsCol", _pointsCol);
        _mat.SetFloat("_PointSize", pointSize);

        // generous bounds so the whole cloud is never frustum-culled
        var bounds = new Bounds(transform.position, Vector3.one * 100f);
        Graphics.DrawMeshInstancedIndirect(_quad, 0, _mat, bounds, _argsBuffer);
    }

    private void OnDisable()
    {
        if (_pointsPos  != null) _pointsPos.Dispose();
        if (_pointsCol  != null) _pointsCol.Dispose();
        if (_argsBuffer != null) _argsBuffer.Dispose();
        if (_quad       != null) Destroy(_quad);
    }
}
