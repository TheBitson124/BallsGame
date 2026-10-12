// Draws every ball of a FunctionGrid in one instanced call (Graphics.RenderMeshPrimitives).
// Per ball, FunctionGrid fills two buffers: _Balls (xyz = position in the grid's local space, w = diameter)
// and _Colors (the color of the ball's type). _GridToWorld / _WorldToGrid place the grid.
Shader "FunctionBalls/Ball"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        CGPROGRAM
        #pragma surface surf Standard vertex:vert addshadow fullforwardshadows
        #pragma instancing_options assumeuniformscaling procedural:setup
        #pragma editor_sync_compilation
        #pragma target 4.5

        fixed4 _Color;
        half _Glossiness;
        half _Metallic;
        float4x4 _GridToWorld;
        float4x4 _WorldToGrid;

        #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
        StructuredBuffer<float4> _Balls;
        StructuredBuffer<float4> _Colors;
        #endif

        struct Input
        {
            float4 ballColor;
        };

        // Builds this instance's object matrices from its buffer entry
        void setup()
        {
            #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            float4 b = _Balls[unity_InstanceID];
            float s = 1.0 / b.w;
            float4x4 local = float4x4(b.w, 0, 0, b.x,  0, b.w, 0, b.y,  0, 0, b.w, b.z,  0, 0, 0, 1);
            float4x4 localInverse = float4x4(s, 0, 0, -b.x * s,  0, s, 0, -b.y * s,  0, 0, s, -b.z * s,  0, 0, 0, 1);
            unity_ObjectToWorld = mul(_GridToWorld, local);
            unity_WorldToObject = mul(localInverse, _WorldToGrid);
            #endif
        }

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
            o.ballColor = _Colors[unity_InstanceID];
            #else
            o.ballColor = float4(1, 1, 1, 1);
            #endif
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            o.Albedo = IN.ballColor.rgb * _Color.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
