using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Kalo.App.ViewModels;
using Kalo.Core.Geometry;

namespace Kalo.App.Viewport;

/// <summary>
/// The object browser's 3D preview: the selected object's model, textured, turning slowly.
/// Drag to turn it yourself, wheel to zoom. Reads MainViewModel.PreviewMesh.
/// </summary>
public sealed class ModelPreview : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private const string VertexShader = @"
in vec3 aPosition;
in vec3 aNormal;
in vec2 aUv;
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec3 vNormal;
out vec2 vUv;
void main() {
    vNormal = mat3(uModel) * aNormal;
    vUv = aUv;
    gl_Position = uViewProj * uModel * vec4(aPosition, 1.0);
}";

    private const string FragmentShader = @"
in vec3 vNormal;
in vec2 vUv;
uniform sampler2D uTex;
uniform float uHasTex;
uniform vec4 uUvXform;
uniform vec3 uColor;
out vec4 fragColor;
void main() {
    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) n = -n;
    float light = 0.35 + 0.5 * max(dot(n, normalize(vec3(0.4, -0.5, 0.75))), 0.0) + 0.25 * max(dot(n, normalize(vec3(-0.6, 0.4, 0.2))), 0.0);
    vec3 albedo = uHasTex > 0.5 ? texture(uTex, vUv * uUvXform.xy + uUvXform.zw).rgb : uColor;
    fragColor = vec4(albedo * light, 1.0);
}";

    private Gl? _gl;
    private uint _program, _vao, _vbo;
    private int _count;
    private int _uViewProj, _uModel, _uTex, _uHasTex, _uUvXform, _uColor;
    private Mesh? _uploaded;
    private readonly Dictionary<string, uint> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _yaw = 0.8f, _pitch = 0.35f, _zoom = 1f;
    private Point? _drag;
    private double _lastInput = -10;
    private MainViewModel? _vm;

    public ModelPreview()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        RequestNextFrameRendering();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PreviewVersion) or nameof(MainViewModel.PreviewMesh))
        {
            _yaw = 0.8f; _pitch = 0.35f; _zoom = 1f;
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new Gl(gl.GetProcAddress);
            _program = _gl.BuildProgram(VertexShader, FragmentShader);
            _uViewProj = _gl.Uniform(_program, "uViewProj");
            _uModel = _gl.Uniform(_program, "uModel");
            _uTex = _gl.Uniform(_program, "uTex");
            _uHasTex = _gl.Uniform(_program, "uHasTex");
            _uUvXform = _gl.Uniform(_program, "uUvXform");
            _uColor = _gl.Uniform(_program, "uColor");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Preview unavailable: " + ex.Message);
            _gl = null;
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl == null) return;
        _gl.DeleteVertexArray(_vao); _gl.DeleteBuffer(_vbo);
        foreach (uint t in _textures.Values) _gl.DeleteTexture(t);
        _textures.Clear();
        _gl.DeleteProgram(_program);
        _uploaded = null;
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        var gl = _gl;
        if (gl == null) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scale)), h = Math.Max(1, (int)(Bounds.Height * scale));
        gl.BindFramebuffer(fb);
        gl.Viewport(0, 0, w, h);
        gl.ClearColor(0.086f, 0.09f, 0.102f, 1f);
        gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        var mesh = _vm?.PreviewMesh;
        if (mesh == null || mesh.TriangleCount == 0) return;
        if (!ReferenceEquals(mesh, _uploaded))
        {
            gl.DeleteVertexArray(_vao); gl.DeleteBuffer(_vbo);
            float[] data = Shapes.FlatShaded(mesh);
            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            _vbo = gl.GenBuffer();
            gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            gl.BufferData(Gl.ARRAY_BUFFER, data, Gl.STATIC_DRAW);
            const int stride = Shapes.Stride * 4;
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, stride, 0);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, stride, 12);
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, stride, 24);
            gl.BindVertexArray(0);
            _count = data.Length / Shapes.Stride;
            _uploaded = mesh;
        }

        double now = _clock.Elapsed.TotalSeconds;
        bool spinning = now - _lastInput > 3;
        if (spinning) _yaw += 0.006f;

        Vector3 center = mesh.Center;
        float radius = Math.Max(0.1f, (mesh.Max - mesh.Min).Length() * 0.5f);
        float distance = radius * 2.6f * _zoom;
        var eye = center + new Vector3(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch)) * distance;
        var view = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitZ);
        float near = Math.Max(0.01f, distance - radius * 2), far = distance + radius * 2, f = 1f / MathF.Tan(0.45f), aspect = (float)w / h;
        var proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);

        gl.Enable(Gl.DEPTH_TEST);
        gl.DepthFunc(Gl.LEQUAL);
        gl.DepthMask(true);
        gl.Disable(Gl.CULL_FACE);
        gl.UseProgram(_program);
        gl.UniformMatrix(_uViewProj, view * proj);
        gl.UniformMatrix(_uModel, Matrix4x4.Identity);
        gl.UniformInt(_uTex, 0);
        gl.Uniform3(_uColor, new Vector3(0.62f, 0.64f, 0.68f));
        gl.BindVertexArray(_vao);
        var groups = mesh.Groups;
        if (groups.Count == 0 || mesh.Uvs == null)
        {
            gl.Uniform1(_uHasTex, 0f);
            gl.DrawArrays(Gl.TRIANGLES, 0, _count);
        }
        else
        {
            foreach (var g in groups)
            {
                if (g.Count <= 0 || g.Start + g.Count > _count) continue;
                var mat = g.Material >= 0 && g.Material < mesh.Materials.Count ? mesh.Materials[g.Material] : null;
                if (mat != null && mat.IsTranslucent && mat.Blend == "additive") continue; // glows and glass sheen
                uint tex = mat != null && !string.IsNullOrEmpty(mat.BaseMap) ? Texture(gl, mat.BaseMap) : 0;
                if (tex != 0)
                {
                    gl.BindTexture(tex);
                    gl.Uniform4(_uUvXform, mat!.ScaleX, mat.ScaleY, mat.OffsetX, mat.OffsetY);
                }
                gl.Uniform1(_uHasTex, tex != 0 ? 1f : 0f);
                gl.DrawArrays(Gl.TRIANGLES, g.Start, g.Count);
            }
        }
        gl.BindVertexArray(0);
        if (spinning || _drag != null) RequestNextFrameRendering();
    }

    private uint Texture(Gl gl, string bitmap)
    {
        if (_textures.TryGetValue(bitmap, out uint t)) return t;
        var image = _vm?.TextureFor(bitmap);
        if (image == null) return 0; // not decoded yet: ask again next frame
        t = gl.CreateTexture(image.Width, image.Height, image.Rgba);
        _textures[bitmap] = t;
        return t;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = e.GetPosition(this);
        _lastInput = _clock.Elapsed.TotalSeconds;
        e.Pointer.Capture(this);
        RequestNextFrameRendering();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } last) return;
        var p = e.GetPosition(this);
        _yaw -= (float)(p.X - last.X) * 0.01f;
        _pitch = Math.Clamp(_pitch + (float)(p.Y - last.Y) * 0.01f, -1.4f, 1.4f);
        _drag = p;
        _lastInput = _clock.Elapsed.TotalSeconds;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
        RequestNextFrameRendering();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 0.9f : 1.1f), 0.3f, 4f);
        _lastInput = _clock.Elapsed.TotalSeconds;
        RequestNextFrameRendering();
        e.Handled = true;
    }
}
