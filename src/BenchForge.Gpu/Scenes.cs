using System.Numerics;
using Silk.NET.OpenGL;

namespace BenchForge.Gpu;

/// <summary>Tens of thousands of lit, instanced spheres in a rotating field — vertex & raster load.</summary>
internal sealed unsafe class GeometryScene : IScene
{
    public string Name => Core.Gpu.GpuScenes.Geometry;
    private uint _prog, _vao, _vbo, _ebo;
    private int _indexCount;
    private const int Instances = 24000;

    private const string Vs = """
        #version 430 core
        layout(location=0) in vec3 aPos;
        uniform mat4 uViewProj;
        uniform float uTime; uniform int uBase;
        out vec3 vNormal; out vec3 vWorld; out vec3 vColor;
        float h(float n) { return fract(sin(n) * 43758.5453); }
        void main() {
            float id = float(gl_InstanceID + uBase);
            float ring = floor(sqrt(id));
            float ang = id * 2.39996 + uTime * (0.15 + 0.1 * h(id));
            float rad = 2.0 + ring * 0.55;
            vec3 center = vec3(cos(ang) * rad, sin(uTime * 0.8 + id * 0.05) * 1.5 + (h(id + 3.0) - 0.5) * 6.0, sin(ang) * rad);
            float s = 0.25 + 0.35 * h(id + 7.0);
            float a = uTime * (h(id + 11.0) * 2.0 - 1.0) * 2.0;
            mat3 rot = mat3(cos(a), 0, -sin(a), 0, 1, 0, sin(a), 0, cos(a));
            vec3 p = rot * (aPos * vec3(s, s * (0.6 + h(id + 5.0)), s));
            vWorld = p + center;
            vNormal = normalize(rot * aPos);
            vColor = 0.5 + 0.5 * cos(6.2831 * (vec3(0.0, 0.33, 0.67) + h(id + 13.0)));
            gl_Position = uViewProj * vec4(vWorld, 1.0);
        }
        """;

    private const string Fs = """
        #version 430 core
        in vec3 vNormal; in vec3 vWorld; in vec3 vColor;
        uniform vec3 uEye; uniform float uTime;
        out vec4 oColor;
        void main() {
            vec3 n = normalize(vNormal);
            vec3 v = normalize(uEye - vWorld);
            vec3 col = vColor * 0.08;
            for (int i = 0; i < 6; i++) {
                float fi = float(i);
                vec3 lp = vec3(cos(uTime * 0.5 + fi * 1.05) * 30.0, 8.0 + 4.0 * sin(fi), sin(uTime * 0.5 + fi * 1.05) * 30.0);
                vec3 lc = 0.5 + 0.5 * cos(vec3(0, 2, 4) + fi);
                vec3 l = lp - vWorld; float d = length(l); l /= d;
                vec3 hv = normalize(l + v);
                float diff = max(dot(n, l), 0.0);
                float spec = pow(max(dot(n, hv), 0.0), 64.0);
                col += (vColor * diff + spec * 0.6) * lc * (40.0 / (1.0 + d * d * 0.02));
            }
            float fog = exp(-length(uEye - vWorld) * 0.012);
            col = mix(vec3(0.02, 0.03, 0.05), col, fog);
            oColor = vec4(col / (1.0 + col), 1.0);
        }
        """;

    public void Init(GL gl, int width, int height)
    {
        _prog = GlUtil.Program(gl, (ShaderType.VertexShader, Vs), (ShaderType.FragmentShader, Fs));
        // UV sphere 40×24 ≈ 1,900 triangles.
        const int seg = 40, rings = 24;
        var verts = new List<float>();
        var idx = new List<uint>();
        for (int r = 0; r <= rings; r++)
        {
            double phi = Math.PI * r / rings;
            for (int s = 0; s <= seg; s++)
            {
                double th = 2 * Math.PI * s / seg;
                verts.Add((float)(Math.Sin(phi) * Math.Cos(th))); verts.Add((float)Math.Cos(phi)); verts.Add((float)(Math.Sin(phi) * Math.Sin(th)));
            }
        }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                uint a = (uint)(r * (seg + 1) + s), b = a + seg + 1;
                idx.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            }
        _indexCount = idx.Count;
        _vao = gl.GenVertexArray(); gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts.ToArray(), BufferUsageARB.StaticDraw);
        _ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx.ToArray(), BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        gl.BindVertexArray(0);
    }

    public void Render(GL gl, double time, int width, int height)
    {
        float t = (float)time;
        gl.Enable(EnableCap.DepthTest);
        gl.ClearColor(0.02f, 0.03f, 0.05f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var eye = new Vector3(MathF.Cos(t * 0.2f) * 70, 25 + MathF.Sin(t * 0.3f) * 10, MathF.Sin(t * 0.2f) * 70);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, width / (float)height, 0.5f, 400f);
        var vp = view * proj;
        gl.UseProgram(_prog);
        gl.UniformMatrix4(gl.GetUniformLocation(_prog, "uViewProj"), 1, false, (float*)&vp);
        gl.Uniform1(gl.GetUniformLocation(_prog, "uTime"), t);
        gl.Uniform3(gl.GetUniformLocation(_prog, "uEye"), eye.X, eye.Y, eye.Z);
        gl.BindVertexArray(_vao);
        int baseLoc = gl.GetUniformLocation(_prog, "uBase");
        const int batches = 6; // smaller submissions keep slow GPUs clear of the driver timeout
        for (int b = 0; b < batches; b++)
        {
            gl.Uniform1(baseLoc, b * (Instances / batches));
            gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedInt, (void*)0, Instances / batches);
            gl.Flush();
        }
        gl.BindVertexArray(0);
        gl.Disable(EnableCap.DepthTest);
    }

    public void Dispose() { }
}

/// <summary>Ray-marched fractal landscape with soft shadows, AO and fog — heavy pixel-shader maths.</summary>
internal sealed class ShadingScene : IScene
{
    public string Name => Core.Gpu.GpuScenes.Shading;
    private uint _prog, _vao;

    private const string Fs = """
        #version 430 core
        in vec2 vUv;
        uniform vec2 uRes; uniform float uTime;
        out vec4 oColor;
        float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
        }
        const mat2 m2 = mat2(0.8, -0.6, 0.6, 0.8);
        float terrain(vec2 p, int oct) {
            float a = 0.0, b = 1.0; vec2 d = vec2(0.0);
            for (int i = 0; i < oct; i++) {
                float n = noise(p);
                vec2 g = vec2(noise(p + vec2(0.01, 0)) - n, noise(p + vec2(0, 0.01)) - n) * 100.0;
                d += g;
                a += b * n / (1.0 + dot(d, d));
                b *= 0.5; p = m2 * p * 2.0;
            }
            return a * 9.0;
        }
        float map(vec3 p) { return p.y - terrain(p.xz * 0.12, 8); }
        float mapLo(vec3 p) { return p.y - terrain(p.xz * 0.12, 5); }
        float march(vec3 ro, vec3 rd) {
            float t = 0.5;
            for (int i = 0; i < 200; i++) {
                float h = map(ro + rd * t);
                if (abs(h) < 0.002 * t || t > 150.0) break;
                t += 0.4 * h;
            }
            return t;
        }
        float shadow(vec3 ro, vec3 rd) {
            float res = 1.0, t = 0.2;
            for (int i = 0; i < 48; i++) {
                float h = mapLo(ro + rd * t);
                res = min(res, 10.0 * h / t);
                t += clamp(h, 0.1, 2.0);
                if (res < 0.001 || t > 60.0) break;
            }
            return clamp(res, 0.0, 1.0);
        }
        vec3 normal(vec3 p, float t) {
            vec2 e = vec2(0.002 * t, 0.0);
            return normalize(vec3(map(p - e.xyy) - map(p + e.xyy), 2.0 * e.x, map(p - e.yyx) - map(p + e.yyx)));
        }
        float ao(vec3 p, vec3 n) {
            float o = 0.0, s = 1.0;
            for (int i = 1; i <= 5; i++) { float d = 0.15 * float(i); o += (d - map(p + n * d)) * s; s *= 0.6; }
            return clamp(1.0 - 1.5 * o, 0.0, 1.0);
        }
        void main() {
            vec2 uv = (vUv * uRes - 0.5 * uRes) / uRes.y;
            float t = uTime * 0.6;
            vec3 ro = vec3(t * 4.0, 0.0, t * 3.0);
            ro.y = terrain(ro.xz * 0.12, 4) + 3.5;
            vec3 ta = ro + vec3(cos(t * 0.3), -0.25, sin(t * 0.3) + 1.0);
            vec3 ww = normalize(ta - ro), uu = normalize(cross(ww, vec3(0, 1, 0))), vv = cross(uu, ww);
            vec3 rd = normalize(uv.x * uu + uv.y * vv + 1.6 * ww);
            vec3 sun = normalize(vec3(0.6, 0.35, 0.5));
            vec3 sky = vec3(0.45, 0.62, 0.85) - rd.y * 0.35;
            sky += vec3(1.0, 0.7, 0.4) * pow(max(dot(rd, sun), 0.0), 16.0) * 0.5;
            float d = march(ro, rd);
            vec3 col = sky;
            if (d < 150.0) {
                vec3 p = ro + rd * d;
                vec3 n = normal(p, d);
                float sh = shadow(p + n * 0.05, sun);
                float occ = ao(p, n);
                vec3 rock = mix(vec3(0.32, 0.27, 0.22), vec3(0.18, 0.32, 0.12), smoothstep(0.6, 0.9, n.y));
                rock = mix(rock, vec3(0.95), smoothstep(6.5, 7.5, p.y + n.y * 2.0));
                float dif = max(dot(n, sun), 0.0) * sh;
                vec3 lin = dif * vec3(1.6, 1.3, 1.0) + occ * (0.25 + 0.25 * n.y) * vec3(0.4, 0.55, 0.8);
                col = rock * lin;
                col = mix(col, sky, 1.0 - exp(-0.0004 * d * d));
            }
            col = pow(col / (1.0 + col), vec3(0.4545));
            oColor = vec4(col, 1.0);
        }
        """;

    public void Init(GL gl, int width, int height)
    {
        _prog = GlUtil.Program(gl, (ShaderType.VertexShader, GlUtil.FullscreenVs), (ShaderType.FragmentShader, Fs));
        _vao = gl.GenVertexArray();
    }

    public void Render(GL gl, double time, int width, int height)
    {
        gl.UseProgram(_prog);
        gl.Uniform2(gl.GetUniformLocation(_prog, "uRes"), (float)width, height);
        gl.Uniform1(gl.GetUniformLocation(_prog, "uTime"), (float)time);
        gl.BindVertexArray(_vao);
        // Draw in horizontal bands: the same total work, but no single GPU command runs long enough to trip
        // Windows' 2-second driver timeout (TDR) on slow GPUs.
        gl.Enable(EnableCap.ScissorTest);
        const int bands = 8;
        for (int i = 0; i < bands; i++)
        {
            int y0 = height * i / bands, y1 = height * (i + 1) / bands;
            gl.Scissor(0, y0, (uint)width, (uint)(y1 - y0));
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Flush();
        }
        gl.Disable(EnableCap.ScissorTest);
    }

    public void Dispose() { }
}

/// <summary>One million particles simulated by a compute shader, drawn as additive points.</summary>
internal sealed unsafe class ComputeScene : IScene
{
    public string Name => Core.Gpu.GpuScenes.Compute;
    private const int Count = 1 << 20;
    private uint _sim, _draw, _buf, _vao;

    private const string Cs = """
        #version 430 core
        layout(local_size_x = 256) in;
        struct P { vec4 pos; vec4 vel; };
        layout(std430, binding = 0) buffer Particles { P p[]; };
        uniform float uTime; uniform float uDt;
        void main() {
            uint i = gl_GlobalInvocationID.x;
            if (i >= p.length()) return;
            vec3 pos = p[i].pos.xyz, vel = p[i].vel.xyz;
            vec3 acc = vec3(0.0);
            for (int k = 0; k < 8; k++) {
                float fk = float(k);
                vec3 a = vec3(sin(uTime * 0.31 + fk * 1.7) * 9.0, cos(uTime * 0.23 + fk * 2.3) * 6.0, sin(uTime * 0.17 + fk * 0.9) * 9.0);
                vec3 d = a - pos; float r2 = dot(d, d) + 0.5;
                acc += d * (25.0 / (r2 * sqrt(r2)));
            }
            acc += cross(vel, vec3(0.0, 1.0, 0.0)) * 0.3 - vel * 0.05;
            vel += acc * uDt;
            pos += vel * uDt;
            if (dot(pos, pos) > 900.0) { pos *= 0.1; vel *= 0.1; }
            p[i].pos = vec4(pos, 1.0); p[i].vel = vec4(vel, 0.0);
        }
        """;

    private const string Vs = """
        #version 430 core
        layout(location=0) in vec4 aPos;
        layout(location=1) in vec4 aVel;
        uniform mat4 uViewProj;
        out vec3 vColor;
        void main() {
            float s = length(aVel.xyz);
            vColor = mix(vec3(0.1, 0.35, 1.0), vec3(1.0, 0.45, 0.1), clamp(s * 0.08, 0.0, 1.0)) * 0.05;
            gl_Position = uViewProj * vec4(aPos.xyz, 1.0);
            gl_PointSize = 2.0;
        }
        """;

    private const string Fs = """
        #version 430 core
        in vec3 vColor; out vec4 oColor;
        void main() { oColor = vec4(vColor, 1.0); }
        """;

    public void Init(GL gl, int width, int height)
    {
        _sim = GlUtil.Program(gl, (ShaderType.ComputeShader, Cs));
        _draw = GlUtil.Program(gl, (ShaderType.VertexShader, Vs), (ShaderType.FragmentShader, Fs));
        var data = new float[Count * 8];
        var rng = new Random(7);
        for (int i = 0; i < Count; i++)
        {
            double th = rng.NextDouble() * Math.PI * 2, r = 4 + rng.NextDouble() * 10;
            data[i * 8] = (float)(Math.Cos(th) * r); data[i * 8 + 1] = (float)(rng.NextDouble() * 4 - 2); data[i * 8 + 2] = (float)(Math.Sin(th) * r); data[i * 8 + 3] = 1;
            data[i * 8 + 4] = (float)(-Math.Sin(th) * 3); data[i * 8 + 6] = (float)(Math.Cos(th) * 3);
        }
        _buf = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _buf);
        gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, data, BufferUsageARB.DynamicCopy);
        _vao = gl.GenVertexArray(); gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _buf);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 32, (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 32, (void*)16);
        gl.BindVertexArray(0);
    }

    public void Render(GL gl, double time, int width, int height)
    {
        float t = (float)time;
        gl.UseProgram(_sim);
        gl.Uniform1(gl.GetUniformLocation(_sim, "uTime"), t);
        gl.Uniform1(gl.GetUniformLocation(_sim, "uDt"), 0.008f);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _buf);
        for (int step = 0; step < 4; step++)
        {
            gl.DispatchCompute((uint)(Count / 256), 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        }
        gl.MemoryBarrier(MemoryBarrierMask.VertexAttribArrayBarrierBit);

        gl.ClearColor(0, 0, 0.01f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        gl.Enable(EnableCap.ProgramPointSize);
        var eye = new Vector3(MathF.Cos(t * 0.15f) * 32, 12, MathF.Sin(t * 0.15f) * 32);
        var vp = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(1.0f, width / (float)height, 0.1f, 200f);
        gl.UseProgram(_draw);
        gl.UniformMatrix4(gl.GetUniformLocation(_draw, "uViewProj"), 1, false, (float*)&vp);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(PrimitiveType.Points, 0, Count);
        gl.BindVertexArray(0);
        gl.Disable(EnableCap.Blend);
    }

    public void Dispose() { }
}

/// <summary>Many blended, textured full-screen layers — fill-rate and memory bandwidth.</summary>
internal sealed unsafe class FillScene : IScene
{
    public string Name => Core.Gpu.GpuScenes.Fill;
    private uint _prog, _vao, _tex;
    private const int Layers = 40;

    private const string Fs = """
        #version 430 core
        in vec2 vUv;
        uniform sampler2D uTex; uniform float uTime; uniform float uLayer;
        out vec4 oColor;
        void main() {
            float a = uTime * (0.05 + uLayer * 0.004) * (mod(uLayer, 2.0) < 1.0 ? 1.0 : -1.0) + uLayer * 0.7;
            mat2 r = mat2(cos(a), -sin(a), sin(a), cos(a));
            vec2 uv = r * (vUv - 0.5) * vec2(1.777, 1.0) * (0.35 + uLayer * 0.012) + 0.5;
            vec4 c = texture(uTex, uv) * 0.55 + texture(uTex, uv * 2.0 + 0.3) * 0.25 + texture(uTex, uv * 4.0 - 0.2) * 0.12 + texture(uTex, uv * 0.5) * 0.08;
            float mask = smoothstep(0.52, 0.62, c.r);
            vec3 tint = 0.55 + 0.45 * cos(vec3(0, 2, 4) + uLayer * 0.35 + uTime * 0.15);
            float vig = smoothstep(1.1, 0.25, length(vUv - 0.5));
            oColor = vec4(tint * (0.4 + c.g) * (0.5 + 0.7 * vig), mask * 0.55);
        }
        """;

    public void Init(GL gl, int width, int height)
    {
        _prog = GlUtil.Program(gl, (ShaderType.VertexShader, GlUtil.FullscreenVs), (ShaderType.FragmentShader, Fs));
        _vao = gl.GenVertexArray();
        const int size = 2048;
        var pixels = new uint[size * size];
        uint s = 2463534242;
        Parallel.For(0, size, y =>
        {
            uint rs = (uint)(y * 2654435761u) | 1;
            for (int x = 0; x < size; x++)
            {
                double fx = x / (double)size * Math.PI * 2, fy = y / (double)size * Math.PI * 2;
                double plasma = Math.Sin(fx * 3 + Math.Sin(fy * 2) * 2) + Math.Sin(fy * 5 + Math.Cos(fx * 3)) + Math.Sin((fx + fy) * 4);
                double rings = Math.Sin(Math.Sqrt(Math.Pow(Math.Sin(fx * 4), 2) + Math.Pow(Math.Sin(fy * 4), 2)) * 18);
                double v = 0.5 + 0.25 * plasma / 3 + 0.25 * rings;
                rs ^= rs << 13; rs ^= rs >> 17; rs ^= rs << 5;
                double grain = (rs & 0xFF) / 255.0 * 0.15;
                byte r = (byte)Math.Clamp((v * 0.9 + grain) * 255, 0, 255);
                byte g = (byte)Math.Clamp((0.5 + 0.5 * Math.Sin(v * 6 + 2) + grain) * 200, 0, 255);
                byte b = (byte)Math.Clamp((1 - v + grain) * 255, 0, 255);
                pixels[y * size + x] = 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
            }
        });
        _ = s;
        _tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        fixed (uint* p = pixels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
    }

    public void Render(GL gl, double time, int width, int height)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.UseProgram(_prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        gl.Uniform1(gl.GetUniformLocation(_prog, "uTex"), 0);
        gl.Uniform1(gl.GetUniformLocation(_prog, "uTime"), (float)time);
        int layerLoc = gl.GetUniformLocation(_prog, "uLayer");
        gl.BindVertexArray(_vao);
        for (int i = 0; i < Layers; i++)
        {
            gl.Uniform1(layerLoc, (float)i);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        gl.Disable(EnableCap.Blend);
    }

    public void Dispose() { }
}

/// <summary>
/// Video memory test: a compute shader writes a known pattern into large buffers and a second pass checks every word,
/// counting mismatches on the GPU. Errors here mean unstable VRAM (memory overclock, heat) or a failing card.
/// </summary>
internal sealed unsafe class VramChecker : IDisposable
{
    private uint _write, _verify, _counter;
    private readonly List<(uint Buffer, long Words)> _chunks = new();
    public long BytesPerPass { get; private set; }
    public long TotalChecked { get; private set; }
    public long TotalErrors { get; private set; }
    private uint _pass;

    private const string WriteCs = """
        #version 430 core
        layout(local_size_x = 256) in;
        layout(std430, binding = 0) buffer Data { uint d[]; };
        uniform uint uSeed; uniform uint uCount;
        uint hash(uint x) { x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16; return x; }
        void main() {
            uint stride = gl_NumWorkGroups.x * 256u;
            for (uint i = gl_GlobalInvocationID.x; i < uCount; i += stride) d[i] = hash(i ^ uSeed) ^ ((uSeed & 1u) == 1u ? 0xFFFFFFFFu : 0u);
        }
        """;

    private const string VerifyCs = """
        #version 430 core
        layout(local_size_x = 256) in;
        layout(std430, binding = 0) buffer Data { uint d[]; };
        layout(std430, binding = 1) buffer Errors { uint errors; uint firstIndex; uint firstExpected; uint firstActual; };
        uniform uint uSeed; uniform uint uCount;
        uint hash(uint x) { x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16; return x; }
        void main() {
            uint stride = gl_NumWorkGroups.x * 256u;
            for (uint i = gl_GlobalInvocationID.x; i < uCount; i += stride) {
                uint e = hash(i ^ uSeed) ^ ((uSeed & 1u) == 1u ? 0xFFFFFFFFu : 0u);
                uint a = d[i];
                if (a != e) {
                    uint n = atomicAdd(errors, 1u);
                    if (n == 0u) { firstIndex = i; firstExpected = e; firstActual = a; }
                }
            }
        }
        """;

    public void Init(GL gl, long targetBytes)
    {
        _write = GlUtil.Program(gl, (ShaderType.ComputeShader, WriteCs));
        _verify = GlUtil.Program(gl, (ShaderType.ComputeShader, VerifyCs));
        gl.GetInteger64((GetPName)GLEnum.MaxShaderStorageBlockSize, out long maxBlock);
        long chunk = Math.Clamp(maxBlock, 16L << 20, 128L << 20) & ~3L;
        long remaining = targetBytes;
        while (remaining > 0)
        {
            long bytes = Math.Min(chunk, remaining);
            uint b = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, b);
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)bytes, null, BufferUsageARB.DynamicCopy);
            if (gl.GetError() != GLEnum.NoError) { gl.DeleteBuffer(b); break; } // out of VRAM: test what we got
            _chunks.Add((b, bytes / 4));
            BytesPerPass += bytes;
            remaining -= bytes;
        }
        _counter = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counter);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, 16, null, BufferUsageARB.DynamicRead);
    }

    /// <summary>Writes and verifies every chunk once. Returns (new errors, detail of the first one).</summary>
    public (long Errors, string? Detail) RunPass(GL gl)
    {
        _pass++;
        uint seed = _pass * 2654435761u;
        var zero = stackalloc uint[4];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counter);
        gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, 16, zero);
        foreach (var (buf, words) in _chunks)
        {
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, buf);
            uint groups = (uint)Math.Min(65535, (words + 255) / 256);
            gl.UseProgram(_write);
            gl.Uniform1(gl.GetUniformLocation(_write, "uSeed"), seed);
            gl.Uniform1(gl.GetUniformLocation(_write, "uCount"), (uint)words);
            gl.DispatchCompute(groups, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.UseProgram(_verify);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _counter);
            gl.Uniform1(gl.GetUniformLocation(_verify, "uSeed"), seed);
            gl.Uniform1(gl.GetUniformLocation(_verify, "uCount"), (uint)words);
            gl.DispatchCompute(groups, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);
        }
        var result = stackalloc uint[4];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counter);
        gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, 16, result);
        TotalChecked += BytesPerPass;
        TotalErrors += result[0];
        return result[0] == 0 ? (0, null) : (result[0], $"first bad word #{result[1]}: expected {result[2]:X8} read {result[3]:X8}");
    }

    public void Dispose() { }
}
