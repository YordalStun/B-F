using Silk.NET.OpenGL;

namespace BenchForge.Gpu;

internal static class GlUtil
{
    public static uint Program(GL gl, params (ShaderType Type, string Source)[] stages)
    {
        var prog = gl.CreateProgram();
        var shaders = new List<uint>();
        foreach (var (type, src) in stages)
        {
            var s = gl.CreateShader(type);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type} compile error: {gl.GetShaderInfoLog(s)}");
            gl.AttachShader(prog, s);
            shaders.Add(s);
        }
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("Link error: " + gl.GetProgramInfoLog(prog));
        foreach (var s in shaders) { gl.DetachShader(prog, s); gl.DeleteShader(s); }
        return prog;
    }

    public const string FullscreenVs = """
        #version 430 core
        out vec2 vUv;
        void main() {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;
}

/// <summary>A test scene: set up once, then draw frames into the bound framebuffer.</summary>
internal interface IScene : IDisposable
{
    string Name { get; }
    void Init(GL gl, int width, int height);
    void Render(GL gl, double time, int width, int height);
}
