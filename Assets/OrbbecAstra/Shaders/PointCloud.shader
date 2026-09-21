Shader "PointCloud/PointCloud"
{
    Properties
    {
        _PointSize ("Point Size (NDC)", Float) = 0.005
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            #include "UnityCG.cginc"

            struct appdata
            {
                // the quad corner lives in the mesh POSITION attribute, range [-0.5, 0.5]
                float3 corner : POSITION;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float3 color : TEXCOORD0;
            };

            StructuredBuffer<float3> _PointsPos;
            StructuredBuffer<float3> _PointsCol;
            float _PointSize;

            v2f vert (appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float3 worldPos = _PointsPos[instanceID];

                // world -> clip, through this GameObject's transform (so you can move/rotate the cloud)
                float4 clipCenter = mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, float4(worldPos, 1.0)));

                // camera-facing billboard: offset in clip space, constant pixel size
                float2 offset = v.corner.xy * (_PointSize * 2.0) * clipCenter.w;
                clipCenter.xy += offset;

                o.pos = clipCenter;
                o.color = _PointsCol[instanceID];
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return fixed4(i.color, 1.0);
            }
            ENDCG
        }
    }
}
