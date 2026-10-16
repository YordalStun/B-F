// BenchForge 3D test shaders for Direct3D 11/12 (compiled at run time with D3DCompile, shader model 5.0) and
// Vulkan (compiled to SPIR-V ahead of time with glslangValidator -D, which defines VULKAN). Straight ports of the OpenGL GLSL scenes,
// so every API does the same work per frame.

#ifdef VULKAN
#define VKB(n) [[vk::binding(n, 0)]]
#else
#define VKB(n)
#endif

VKB(0) cbuffer Params : register(b0)
{
    float4x4 uViewProj;
    float4 uEye;
    float uTime; float uDt; float uLayer; uint uBase;
    float2 uRes; uint uSeed; uint uCount;
    float4 uRect;
    float4 uColor;
};

VKB(1) Texture2D uTex : register(t0);
VKB(2) SamplerState uSmp : register(s0);

struct Particle { float4 pos; float4 vel; };
VKB(3) StructuredBuffer<Particle> uParticlesIn : register(t1);
VKB(4) RWStructuredBuffer<Particle> uParticles : register(u0);
VKB(5) RWStructuredBuffer<uint> uData : register(u1);
VKB(6) RWStructuredBuffer<uint> uErrors : register(u2);

// ------------------------------------------------------------------ helpers
float h1(float n) { return frac(sin(n) * 43758.5453); }

struct FullOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

FullOut FullVS(uint id : SV_VertexID)
{
    FullOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.uv = p;
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

// ------------------------------------------------------------------ Geometry: instanced lit spheres
struct GeoOut { float4 pos : SV_Position; float3 normal : NORMAL; float3 world : TEXCOORD0; float3 color : COLOR; };

GeoOut GeoVS(float3 aPos : POSITION, uint inst : SV_InstanceID)
{
    GeoOut o;
    float id = (float)(inst + uBase);
    float ring = floor(sqrt(id));
    float ang = id * 2.39996 + uTime * (0.15 + 0.1 * h1(id));
    float rad = 2.0 + ring * 0.55;
    float3 center = float3(cos(ang) * rad, sin(uTime * 0.8 + id * 0.05) * 1.5 + (h1(id + 3.0) - 0.5) * 6.0, sin(ang) * rad);
    float s = 0.25 + 0.35 * h1(id + 7.0);
    float a = uTime * (h1(id + 11.0) * 2.0 - 1.0) * 2.0;
    float3x3 rot = float3x3(cos(a), 0, -sin(a), 0, 1, 0, sin(a), 0, cos(a));
    float3 p = mul(aPos * float3(s, s * (0.6 + h1(id + 5.0)), s), rot);
    o.world = p + center;
    o.normal = normalize(mul(aPos, rot));
    o.color = 0.5 + 0.5 * cos(6.2831 * (float3(0.0, 0.33, 0.67) + h1(id + 13.0)));
    o.pos = mul(uViewProj, float4(o.world, 1.0));
    return o;
}

float4 GeoPS(GeoOut i) : SV_Target
{
    float3 n = normalize(i.normal);
    float3 v = normalize(uEye.xyz - i.world);
    float3 col = i.color * 0.08;
    [loop] for (int k = 0; k < 6; k++)
    {
        float fi = (float)k;
        float3 lp = float3(cos(uTime * 0.5 + fi * 1.05) * 30.0, 8.0 + 4.0 * sin(fi), sin(uTime * 0.5 + fi * 1.05) * 30.0);
        float3 lc = 0.5 + 0.5 * cos(float3(0, 2, 4) + fi);
        float3 l = lp - i.world; float d = length(l); l /= d;
        float3 hv = normalize(l + v);
        float diff = max(dot(n, l), 0.0);
        float spec = pow(max(dot(n, hv), 0.0), 64.0);
        col += (i.color * diff + spec * 0.6) * lc * (40.0 / (1.0 + d * d * 0.02));
    }
    float fog = exp(-length(uEye.xyz - i.world) * 0.012);
    col = lerp(float3(0.02, 0.03, 0.05), col, fog);
    return float4(col / (1.0 + col), 1.0);
}

// ------------------------------------------------------------------ Shading: ray-marched landscape
float hash2(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
float noise2(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash2(i), hash2(i + float2(1, 0)), u.x), lerp(hash2(i + float2(0, 1)), hash2(i + float2(1, 1)), u.x), u.y);
}
static const float2x2 m2 = float2x2(0.8, -0.6, 0.6, 0.8);
float terrain(float2 p, int oct)
{
    float a = 0.0, b = 1.0; float2 d = float2(0.0, 0.0);
    [loop] for (int i = 0; i < oct; i++)
    {
        float n = noise2(p);
        float2 g = float2(noise2(p + float2(0.01, 0)) - n, noise2(p + float2(0, 0.01)) - n) * 100.0;
        d += g;
        a += b * n / (1.0 + dot(d, d));
        b *= 0.5; p = mul(p, m2) * 2.0;
    }
    return a * 9.0;
}
float mapHi(float3 p) { return p.y - terrain(p.xz * 0.12, 8); }
float mapLo(float3 p) { return p.y - terrain(p.xz * 0.12, 5); }
float march(float3 ro, float3 rd)
{
    float t = 0.5;
    [loop] for (int i = 0; i < 200; i++)
    {
        float h = mapHi(ro + rd * t);
        if (abs(h) < 0.002 * t || t > 150.0) break;
        t += 0.4 * h;
    }
    return t;
}
float softShadow(float3 ro, float3 rd)
{
    float res = 1.0, t = 0.2;
    [loop] for (int i = 0; i < 48; i++)
    {
        float h = mapLo(ro + rd * t);
        res = min(res, 10.0 * h / t);
        t += clamp(h, 0.1, 2.0);
        if (res < 0.001 || t > 60.0) break;
    }
    return clamp(res, 0.0, 1.0);
}
float3 terrainNormal(float3 p, float t)
{
    float2 e = float2(0.002 * t, 0.0);
    return normalize(float3(mapHi(p - e.xyy) - mapHi(p + e.xyy), 2.0 * e.x, mapHi(p - e.yyx) - mapHi(p + e.yyx)));
}
float ambientOcc(float3 p, float3 n)
{
    float o = 0.0, s = 1.0;
    [loop] for (int i = 1; i <= 5; i++) { float d = 0.15 * (float)i; o += (d - mapHi(p + n * d)) * s; s *= 0.6; }
    return clamp(1.0 - 1.5 * o, 0.0, 1.0);
}

float4 ShadePS(FullOut i) : SV_Target
{
    float2 uv = (i.uv * uRes - 0.5 * uRes) / uRes.y;
    float t = uTime * 0.6;
    float3 ro = float3(t * 4.0, 0.0, t * 3.0);
    ro.y = terrain(ro.xz * 0.12, 4) + 3.5;
    float3 ta = ro + float3(cos(t * 0.3), -0.25, sin(t * 0.3) + 1.0);
    float3 ww = normalize(ta - ro), uu = normalize(cross(ww, float3(0, 1, 0))), vv = cross(uu, ww);
    float3 rd = normalize(uv.x * uu + uv.y * vv + 1.6 * ww);
    float3 sun = normalize(float3(0.6, 0.35, 0.5));
    float3 sky = float3(0.45, 0.62, 0.85) - rd.y * 0.35;
    sky += float3(1.0, 0.7, 0.4) * pow(max(dot(rd, sun), 0.0), 16.0) * 0.5;
    float d = march(ro, rd);
    float3 col = sky;
    if (d < 150.0)
    {
        float3 p = ro + rd * d;
        float3 n = terrainNormal(p, d);
        float sh = softShadow(p + n * 0.05, sun);
        float occ = ambientOcc(p, n);
        float3 rock = lerp(float3(0.32, 0.27, 0.22), float3(0.18, 0.32, 0.12), smoothstep(0.6, 0.9, n.y));
        rock = lerp(rock, float3(0.95, 0.95, 0.95), smoothstep(6.5, 7.5, p.y + n.y * 2.0));
        float dif = max(dot(n, sun), 0.0) * sh;
        float3 lin = dif * float3(1.6, 1.3, 1.0) + occ * (0.25 + 0.25 * n.y) * float3(0.4, 0.55, 0.8);
        col = rock * lin;
        col = lerp(col, sky, 1.0 - exp(-0.0004 * d * d));
    }
    col = pow(col / (1.0 + col), float3(0.4545, 0.4545, 0.4545));
    return float4(col, 1.0);
}

// ------------------------------------------------------------------ Compute: 1M particles
[numthreads(256, 1, 1)]
void SimCS(uint3 gid : SV_DispatchThreadID)
{
    uint i = gid.x;
    if (i >= uCount) return;
    float3 pos = uParticles[i].pos.xyz, vel = uParticles[i].vel.xyz;
    float3 acc = float3(0, 0, 0);
    [loop] for (int k = 0; k < 8; k++)
    {
        float fk = (float)k;
        float3 at = float3(sin(uTime * 0.31 + fk * 1.7) * 9.0, cos(uTime * 0.23 + fk * 2.3) * 6.0, sin(uTime * 0.17 + fk * 0.9) * 9.0);
        float3 d = at - pos; float r2 = dot(d, d) + 0.5;
        acc += d * (25.0 / (r2 * sqrt(r2)));
    }
    acc += cross(vel, float3(0.0, 1.0, 0.0)) * 0.3 - vel * 0.05;
    vel += acc * uDt;
    pos += vel * uDt;
    if (dot(pos, pos) > 900.0) { pos *= 0.1; vel *= 0.1; }
    uParticles[i].pos = float4(pos, 1.0);
    uParticles[i].vel = float4(vel, 0.0);
}

// Each particle is a 2×2-pixel quad (the same size as GL's gl_PointSize = 2).
struct PartOut { float4 pos : SV_Position; float3 color : COLOR; };
PartOut PartVS(uint id : SV_VertexID)
{
    static const float2 corner[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };
    Particle p = uParticlesIn[id / 6];
    PartOut o;
    float s = length(p.vel.xyz);
    o.color = lerp(float3(0.1, 0.35, 1.0), float3(1.0, 0.45, 0.1), clamp(s * 0.08, 0.0, 1.0)) * 0.05;
    float4 c = mul(uViewProj, float4(p.pos.xyz, 1.0));
    c.xy += corner[id % 6] / uRes * c.w;
    o.pos = c;
    return o;
}
float4 PartPS(PartOut i) : SV_Target { return float4(i.color, 1.0); }

// ------------------------------------------------------------------ Fill-rate: blended textured layers
float4 FillPS(FullOut i) : SV_Target
{
    float a = uTime * (0.05 + uLayer * 0.004) * (fmod(uLayer, 2.0) < 1.0 ? 1.0 : -1.0) + uLayer * 0.7;
    float2x2 r = float2x2(cos(a), -sin(a), sin(a), cos(a));
    float2 uv = mul((i.uv - 0.5) * float2(1.777, 1.0) * (0.35 + uLayer * 0.012), r) + 0.5;
    float4 c = uTex.Sample(uSmp, uv) * 0.55 + uTex.Sample(uSmp, uv * 2.0 + 0.3) * 0.25 + uTex.Sample(uSmp, uv * 4.0 - 0.2) * 0.12 + uTex.Sample(uSmp, uv * 0.5) * 0.08;
    float mask = smoothstep(0.52, 0.62, c.r);
    float3 tint = 0.55 + 0.45 * cos(float3(0, 2, 4) + uLayer * 0.35 + uTime * 0.15);
    float vig = smoothstep(1.1, 0.25, length(i.uv - 0.5));
    return float4(tint * (0.4 + c.g) * (0.5 + 0.7 * vig), mask * 0.55);
}

// ------------------------------------------------------------------ VRAM check
uint vhash(uint x) { x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16; return x; }
uint expected(uint i) { return vhash(i ^ uSeed) ^ ((uSeed & 1u) == 1u ? 0xFFFFFFFFu : 0u); }

[numthreads(256, 1, 1)]
void VramWriteCS(uint3 gid : SV_DispatchThreadID)
{
    for (uint i = gid.x; i < uCount; i += uBase) uData[i] = expected(i);
}

[numthreads(256, 1, 1)]
void VramVerifyCS(uint3 gid : SV_DispatchThreadID)
{
    for (uint i = gid.x; i < uCount; i += uBase)
    {
        uint e = expected(i);
        uint a = uData[i];
        if (a != e)
        {
            uint n;
            InterlockedAdd(uErrors[0], 1u, n);
            if (n == 0u) { uErrors[1] = i; uErrors[2] = e; uErrors[3] = a; }
        }
    }
}

// ------------------------------------------------------------------ presenting: scaled copy + progress bar
float4 BlitPS(FullOut i) : SV_Target { return uTex.Sample(uSmp, float2(i.uv.x, 1.0 - i.uv.y)); }

float4 RectVS(uint id : SV_VertexID) : SV_Position
{
    static const float2 corner[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(1, 1), float2(0, 1) };
    float2 p = uRect.xy + corner[id] * uRect.zw;        // 0..1, y down
    return float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
}
float4 RectPS() : SV_Target { return uColor; }
