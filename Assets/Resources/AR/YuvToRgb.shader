// XREAL EyeカメラのYUV420 (Y/U/V別テクスチャ、Alpha8形式) をRGBに変換するシェーダー。
// EyeCameraServiceがGraphics.BlitでRenderTextureに焼いてプレビュー表示とAI入力に使う。
// _FlipY: 実機でプレビューが上下逆だったら1にする (Yプレーンはbottom-up格納のため環境で向きが変わる)
Shader "Canbatsu/YuvToRgb"
{
    Properties
    {
        _YTex ("Y (輝度)", 2D) = "black" {}
        _UTex ("U", 2D) = "gray" {}
        _VTex ("V", 2D) = "gray" {}
        _FlipY ("上下反転", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _YTex;
            sampler2D _UTex;
            sampler2D _VTex;
            float _FlipY;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                if (_FlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Alpha8テクスチャは値がaチャンネルに入っている
                float y = tex2D(_YTex, i.uv).a;
                float u = tex2D(_UTex, i.uv).a - 0.5;
                float v = tex2D(_VTex, i.uv).a - 0.5;

                // BT.601の変換式
                float3 rgb;
                rgb.r = y + 1.402 * v;
                rgb.g = y - 0.344 * u - 0.714 * v;
                rgb.b = y + 1.772 * u;
                return fixed4(saturate(rgb), 1.0);
            }
            ENDCG
        }
    }
}
