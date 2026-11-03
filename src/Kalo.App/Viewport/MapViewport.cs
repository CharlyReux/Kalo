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
/// Kalo's 3D map view: the level geometry and every object placement (its render model, or a box sized by its
/// bounding radius), read from the tags and cached; a fly camera, click to select, drag to move.
/// Shading like Blender's viewport: wireframe, solid, or textured with the shaders' color maps, plus an edge overlay.
///
/// Camera: right mouse + drag looks around, WASD/QE fly (Shift = faster), wheel moves forward, middle mouse pans.
/// Objects: click selects, drag moves along the ground (Shift: up/down, Ctrl: snap to 0.25),
/// R / Shift+R turns 15°, Ctrl+D duplicates, Delete removes, F frames the selection. Z cycles the shading.
/// </summary>
public sealed class MapViewport : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>
    /// The GL surface draws nothing Avalonia can see, so without this the control is "empty" for hit testing
    /// and never receives the mouse (no look, no select, no focus for WASD).
    /// </summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private const string VertexShader = @"
in vec3 aPosition;
in vec3 aNormal;
in vec2 aUv;
in vec3 aBary;
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec3 vNormal;
out vec3 vWorld;
out vec2 vUv;
out vec3 vBary;
void main() {
    vec4 w = uModel * vec4(aPosition, 1.0);
    vWorld = w.xyz;
    vNormal = mat3(uModel) * aNormal;
    vUv = aUv;
    vBary = aBary;
    gl_Position = uViewProj * w;
}";

    // uMode: 0 wireframe (edges only, see-through), 1 solid (blockout shading), 2 textured (color maps).
    // uWire: edge overlay on solid/textured. uLit = 0 draws flat uColor (outlines, handles).
    private const string FragmentShader = @"
in vec3 vNormal;
in vec3 vWorld;
in vec2 vUv;
in vec3 vBary;
uniform vec4 uColor;
uniform vec3 uEye;
uniform float uLit;
uniform float uMode;
uniform float uWire;
uniform float uHasTex;
uniform float uTint;
uniform vec4 uUvXform;
uniform sampler2D uTex;
uniform float uBlend;      // textured: 0 opaque, 1 alpha blend, 2 additive, 3 multiply
uniform float uAlphaTest;
// Terrain: uTex is the blend map; its r, g, b, a weigh the 4 material layers.
uniform float uTerrain;
uniform sampler2D uL0;
uniform sampler2D uL1;
uniform sampler2D uL2;
uniform sampler2D uL3;
uniform vec4 uLX0;
uniform vec4 uLX1;
uniform vec4 uLX2;
uniform vec4 uLX3;
uniform vec4 uLayerOn;
out vec4 fragColor;

float edge() {
    vec3 d = fwidth(vBary);
    vec3 a = smoothstep(vec3(0.0), d * 1.3, vBary);
    return 1.0 - min(min(a.x, a.y), a.z);
}

void main() {
    if (uLit < 0.5) { fragColor = uColor; return; }
    vec3 bg = vec3(0.11, 0.115, 0.13);
    float fog = clamp(length(vWorld - uEye) / 600.0, 0.0, 0.55);
    if (uMode < 0.5) {
        float e = edge();
        if (e < 0.04) discard;
        fragColor = vec4(mix(uColor.rgb * 1.15, bg, fog * 0.7), e);
        return;
    }
    vec3 n = normalize(vNormal);
    if (dot(n, uEye - vWorld) < 0.0) n = -n;
    float alpha = uColor.a;
    float sun = max(dot(n, normalize(vec3(0.45, 0.3, 0.84))), 0.0);
    float head = abs(dot(n, normalize(uEye - vWorld)));
    vec3 c;
    if (uMode < 1.5) {
        // Floors warm, walls cool, ceilings dark: reads like a blockout.
        vec3 tint = mix(vec3(0.78, 0.82, 0.92), vec3(0.95, 0.9, 0.8), clamp(n.z, 0.0, 1.0));
        tint = mix(tint, vec3(0.55, 0.58, 0.65), clamp(-n.z, 0.0, 1.0));
        c = uColor.rgb * tint * (0.12 + 0.55 * sun + 0.33 * head);
        // Faint 1-unit grid on floors helps judge distances.
        vec2 g = abs(fract(vWorld.xy) - 0.5);
        float line = smoothstep(0.48, 0.5, max(g.x, g.y)) * clamp(n.z, 0.0, 1.0) * 0.12;
        c *= 1.0 - line;
    } else {
        vec4 t = uHasTex > 0.5 ? texture(uTex, vUv * uUvXform.xy + uUvXform.zw) : uColor;
        if (uTerrain > 0.5) {
            vec4 w = (uHasTex > 0.5 ? t : vec4(1.0)) * uLayerOn;
            float sum = dot(w, vec4(1.0));
            if (sum < 0.001) { w = uLayerOn; sum = dot(w, vec4(1.0)); }
            w /= max(sum, 0.001);
            vec3 mixed = vec3(0.0);
            if (w.x > 0.0) mixed += w.x * texture(uL0, vUv * uLX0.xy + uLX0.zw).rgb;
            if (w.y > 0.0) mixed += w.y * texture(uL1, vUv * uLX1.xy + uLX1.zw).rgb;
            if (w.z > 0.0) mixed += w.z * texture(uL2, vUv * uLX2.xy + uLX2.zw).rgb;
            if (w.w > 0.0) mixed += w.w * texture(uL3, vUv * uLX3.xy + uLX3.zw).rgb;
            t = vec4(mixed, 1.0);
        }
        if (uAlphaTest > 0.5 && t.a < 0.5) discard;
        vec3 albedo = mix(t.rgb, uColor.rgb, uTint);
        if (uBlend > 1.5) {
            // Additive and multiply surfaces (glass sheen, lights, grime) are drawn unlit, faded with distance.
            // The game tints additive layers down (tint colors are not read here), so they are dimmed to stay subtle.
            c = uBlend > 2.5 ? mix(albedo, vec3(1.0), fog) : albedo * 0.35 * (1.0 - fog);
            fragColor = vec4(c, 1.0);
            return;
        }
        c = albedo * (0.3 + 0.5 * sun + 0.4 * head);
        alpha = uBlend > 0.5 ? t.a : 1.0;
    }
    if (uWire > 0.5) c = mix(c, c * 0.25 + vec3(0.02), edge() * 0.85);
    c = mix(c, bg, fog);
    fragColor = vec4(c, alpha);
}";

    private Gl? _gl;
    private string? _glError;
    private uint _program;
    private int _uViewProj, _uModel, _uColor, _uEye, _uLit, _uMode, _uWire, _uHasTex, _uTint, _uUvXform, _uTex, _uBlend, _uAlphaTest, _uTerrain, _uLayerOn;
    private readonly int[] _uLayer = new int[4], _uLayerXform = new int[4];
    private GpuMesh? _bsp;
    private int _bspVersion = -1;
    private uint _cubeVao, _cubeVbo, _edgeVao, _edgeVbo;
    private readonly Dictionary<Mesh, GpuMesh> _models = new(ReferenceEqualityComparer.Instance);
    /// <summary>GL textures by bitmap tag; 0 = no image (retried when the view model has more textures).</summary>
    private readonly Dictionary<string, uint> _textures = new(StringComparer.OrdinalIgnoreCase);
    private int _texturesVersion = -1;

    /// <summary>A mesh on the GPU: expanded triangles plus the material groups to draw them by.</summary>
    private sealed record GpuMesh(uint Vao, uint Vbo, int Count, Mesh Source);

    private Vector3 _eye = new(0, 0, 20);
    private float _yaw = 0.6f, _pitch = -0.35f;
    private const float Fov = 1.0f;
    private readonly HashSet<Key> _keys = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private bool _cameraPlaced;

    private enum Drag { None, Look, Pan, Move, Axis, Turn }
    private Drag _drag;
    private Point _last;
    private PlacementItem? _dragItem;
    private Vector3 _grabOffset;
    private bool _dragVertical, _moved;
    private PlacementItem? _shiftToggle;
    private uint _gizmoVao, _gizmoVbo;
    private const int RingSegments = 48;
    private int _hoverPart = -1;           // 0..2 axis, 3 ring
    private Vector3 _axis, _axisOrigin;
    private float _axisT0, _turnAngle0;
    private Dictionary<PlacementItem, Vector3> _startPositions = new();
    private Dictionary<PlacementItem, float> _startYaws = new();

    public MapViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ---- drag & drop: tags from the project tree -------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        string? tag = e.Data.GetText();
        e.DragEffects = tag != null && MapEditorViewModel.CategoryFor(tag) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var map = Map;
        string? tag = e.Data.GetText();
        if (map == null || tag == null) return;
        var (o, d) = Ray(e.GetPosition(this));
        Vector3? hit = HitScene(o, d);
        Vector3 at = hit is { } h && Vector3.Distance(h, o) < 60f ? h : o + d * 6f;
        await map.AddTagAtAsync(tag.Trim(), at + new Vector3(0, 0, 0.05f));
        RequestNextFrameRendering();
    }

    private MapEditorViewModel? Map => DataContext as MapEditorViewModel;
    private MapEditorViewModel? _subscribed;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed != null)
        {
            _subscribed.SceneChanged -= OnSceneChanged;
            _subscribed.FocusRequested -= OnFocusRequested;
            _subscribed.FrameAllRequested -= OnFrameAll;
            _subscribed.CameraRequested -= OnCameraRequested;
            _subscribed.CaptureRequested -= OnCaptureRequested;
            // Another view may already show this map (moved to the other editor group): keep its provider.
            if (_subscribed.PlacementPointProvider is { } provider && ReferenceEquals(provider.Target, this)) _subscribed.PlacementPointProvider = null;
            if (_subscribed.PlayFromProvider is { } play && ReferenceEquals(play.Target, this)) _subscribed.PlayFromProvider = null;
            if (_subscribed.PlayFromCheck is { } check && ReferenceEquals(check.Target, this)) _subscribed.PlayFromCheck = null;
        }
        _subscribed = Map;
        if (_subscribed != null)
        {
            _subscribed.SceneChanged += OnSceneChanged;
            _subscribed.FocusRequested += OnFocusRequested;
            _subscribed.FrameAllRequested += OnFrameAll;
            _subscribed.CameraRequested += OnCameraRequested;
            _subscribed.CaptureRequested += OnCaptureRequested;
            _subscribed.PlacementPointProvider = PlacementPoint;
            _subscribed.PlayFromProvider = PlayFromPoint;
            _subscribed.PlayFromCheck = PlayFromProblem;
            _cameraPlaced = false;
        }
        RequestNextFrameRendering();
    }

    private void OnSceneChanged() => RequestNextFrameRendering();

    private (string Path, TaskCompletionSource<bool> Done)? _capture;

    private void OnCaptureRequested(string path, TaskCompletionSource<bool> done)
    {
        _capture = (path, done);
        RequestNextFrameRendering();
    }

    /// <summary>Saves what was just drawn (rows come bottom-up from GL).</summary>
    private void SaveCapture(Gl gl, int w, int h)
    {
        if (_capture is not { } c) return;
        _capture = null;
        try
        {
            byte[] px = gl.ReadPixels(w, h);
            using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Opaque);
            using (var fb = bitmap.Lock())
            {
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(px, (h - 1 - y) * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c.Path))!);
            bitmap.Save(c.Path);
            c.Done.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("View capture failed: " + ex.Message);
            c.Done.TrySetResult(false);
        }
    }

    private void OnCameraRequested(Vector3 eye, float yawDegrees, float pitchDegrees)
    {
        _eye = eye;
        _yaw = yawDegrees * MathF.PI / 180f; // ManagedBlam gives angles in degrees
        _pitch = pitchDegrees * MathF.PI / 180f;
        _cameraPlaced = true;
        RequestNextFrameRendering();
    }

    private void OnFrameAll()
    {
        _cameraPlaced = false;
        RequestNextFrameRendering();
    }

    private void OnFocusRequested(Vector3 target, float radius)
    {
        float distance = Math.Max(radius * 6f, 5f);
        _eye = target - Forward * distance;
        RequestNextFrameRendering();
    }

    // ---- GL lifetime --------------------------------------------------------------------------

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new Gl(gl.GetProcAddress);
            _program = _gl.BuildProgram(VertexShader, FragmentShader);
            _uViewProj = _gl.Uniform(_program, "uViewProj");
            _uModel = _gl.Uniform(_program, "uModel");
            _uColor = _gl.Uniform(_program, "uColor");
            _uEye = _gl.Uniform(_program, "uEye");
            _uLit = _gl.Uniform(_program, "uLit");
            _uMode = _gl.Uniform(_program, "uMode");
            _uWire = _gl.Uniform(_program, "uWire");
            _uHasTex = _gl.Uniform(_program, "uHasTex");
            _uTint = _gl.Uniform(_program, "uTint");
            _uUvXform = _gl.Uniform(_program, "uUvXform");
            _uTex = _gl.Uniform(_program, "uTex");
            _uBlend = _gl.Uniform(_program, "uBlend");
            _uAlphaTest = _gl.Uniform(_program, "uAlphaTest");
            _uTerrain = _gl.Uniform(_program, "uTerrain");
            _uLayerOn = _gl.Uniform(_program, "uLayerOn");
            for (int l = 0; l < 4; l++)
            {
                _uLayer[l] = _gl.Uniform(_program, "uL" + l);
                _uLayerXform[l] = _gl.Uniform(_program, "uLX" + l);
            }
            (_cubeVao, _cubeVbo) = Upload(Shapes.CubeTriangles());
            (_edgeVao, _edgeVbo) = Upload(Shapes.CubeEdges());
            (_gizmoVao, _gizmoVbo) = Upload(Shapes.Gizmo(RingSegments));
            Map?.ReportRenderer("OpenGL " + _gl.Version);
        }
        catch (Exception ex)
        {
            _glError = ex.Message;
            Map?.ReportRenderer("3D view unavailable: " + ex.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl == null) return;
        Free(_bsp); _bsp = null;
        _gl.DeleteVertexArray(_cubeVao); _gl.DeleteBuffer(_cubeVbo);
        _gl.DeleteVertexArray(_edgeVao); _gl.DeleteBuffer(_edgeVbo);
        _gl.DeleteVertexArray(_gizmoVao); _gl.DeleteBuffer(_gizmoVbo);
        foreach (var m in _models.Values) Free(m);
        _models.Clear();
        foreach (uint t in _textures.Values) _gl.DeleteTexture(t);
        _textures.Clear();
        _texturesVersion = -1;
        _gl.DeleteProgram(_program);
        _cubeVao = _cubeVbo = _edgeVao = _edgeVbo = 0;
        _bspVersion = -1;
        _gl = null;
    }

    /// <summary>Uploads interleaved vertices: position, normal, uv, barycentric (Shapes.Stride floats each).</summary>
    private (uint vao, uint vbo) Upload(float[] data)
    {
        var gl = _gl!;
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(Gl.ARRAY_BUFFER, vbo);
        gl.BufferData(Gl.ARRAY_BUFFER, data, Gl.STATIC_DRAW);
        const int stride = Shapes.Stride * 4;
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, stride, 0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, stride, 12);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 2, stride, 24);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 3, stride, 32);
        gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private GpuMesh UploadMesh(Mesh mesh)
    {
        float[] data = Shapes.FlatShaded(mesh);
        var (vao, vbo) = Upload(data);
        return new GpuMesh(vao, vbo, data.Length / Shapes.Stride, mesh);
    }

    private void Free(GpuMesh? m)
    {
        if (m == null || _gl == null) return;
        _gl.DeleteVertexArray(m.Vao);
        _gl.DeleteBuffer(m.Vbo);
    }

    /// <summary>The GL texture for a bitmap, created the first time it is needed (0 if there is no image yet).</summary>
    private uint TextureFor(MapEditorViewModel map, string bitmap)
    {
        if (_textures.TryGetValue(bitmap, out uint t)) return t;
        var image = map.TextureFor(bitmap);
        t = image == null ? 0 : _gl!.CreateTexture(image.Width, image.Height, image.Rgba);
        _textures[bitmap] = t;
        return t;
    }

    /// <summary>The level, then every visible object (its model, or a box when it has none).</summary>
    private void DrawScene(Gl gl, MapEditorViewModel map, Vector4 bspColor, bool translucentPass)
    {
        if (_bsp != null && map.ShowGeometry)
        {
            gl.UniformMatrix(_uModel, Matrix4x4.Identity);
            gl.Uniform4(_uColor, bspColor.X, bspColor.Y, bspColor.Z, bspColor.W);
            DrawMesh(gl, map, _bsp, 0f, translucentPass, bspColor);
        }
        foreach (var item in map.VisibleItems)
        {
            bool isSelected = map.IsSelected(item);
            var color = isSelected ? new Vector4(1f, 0.78f, 0.25f, 1f) : new Vector4(item.Color, 1f);
            gl.Uniform4(_uColor, color.X, color.Y, color.Z, color.W);
            var model = item.Model;
            if (model != null && model.TriangleCount > 0)
            {
                if (!_models.TryGetValue(model, out var gpu))
                {
                    gpu = UploadMesh(model);
                    _models[model] = gpu;
                }
                gl.UniformMatrix(_uModel, item.ModelMatrix);
                DrawMesh(gl, map, gpu, isSelected ? 0.45f : 0f, translucentPass, color);
            }
            else if (!translucentPass)
            {
                gl.Uniform1(_uHasTex, 0f);
                gl.UniformMatrix(_uModel, item.BoxMatrix);
                gl.BindVertexArray(_cubeVao);
                gl.DrawArrays(Gl.TRIANGLES, 0, 36);
            }
        }
    }

    /// <summary>
    /// Draws a mesh in the current shading. Textured draws one call per material group with its color map, in two
    /// passes like the game: opaque (and alpha-tested) surfaces first, then glass, decals and lights blended on top.
    /// Solid and wireframe draw everything at once. Tint mixes the object color in (selection highlight).
    /// </summary>
    private void DrawMesh(Gl gl, MapEditorViewModel map, GpuMesh m, float tint, bool translucentPass, Vector4 color)
    {
        var groups = m.Source.Groups;
        bool textured = map.Shading == ViewShading.Textured && groups.Count > 0 && m.Source.Uvs != null;
        if (!textured)
        {
            if (translucentPass) return;
            gl.BindVertexArray(m.Vao);
            gl.Uniform1(_uHasTex, 0f);
            gl.DrawArrays(Gl.TRIANGLES, 0, m.Count);
            return;
        }
        gl.BindVertexArray(m.Vao);
        gl.Uniform1(_uTint, tint);
        var materials = m.Source.Materials;
        foreach (var g in groups)
        {
            if (g.Count <= 0 || g.Start < 0 || g.Start + g.Count > m.Count) continue;
            var mat = g.Material >= 0 && g.Material < materials.Count ? materials[g.Material] : null;
            bool translucent = mat?.IsTranslucent == true;
            if (translucent != translucentPass) continue;
            uint tex = 0;
            bool terrain = false;
            if (mat != null && mat.IsTerrain)
            {
                // Blend map on unit 0, the 4 materials on units 1..4.
                var on = new float[4];
                for (int l = 0; l < 4; l++)
                {
                    var layer = l < mat.Layers.Count ? mat.Layers[l] : null;
                    uint lt = layer != null ? TextureFor(map, layer.Bitmap) : 0;
                    if (lt == 0) continue;
                    on[l] = 1f;
                    gl.BindTexture(lt, 1 + l);
                    gl.Uniform4(_uLayerXform[l], layer!.ScaleX, layer.ScaleY, layer.OffsetX, layer.OffsetY);
                }
                if (on.Any(v => v > 0))
                {
                    terrain = true;
                    tex = TextureFor(map, mat.BlendMap!.Bitmap);
                    gl.Uniform4(_uLayerOn, on[0], on[1], on[2], on[3]);
                    if (tex != 0)
                    {
                        gl.BindTexture(tex, 0);
                        gl.Uniform4(_uUvXform, mat.BlendMap.ScaleX, mat.BlendMap.ScaleY, mat.BlendMap.OffsetX, mat.BlendMap.OffsetY);
                    }
                    gl.Uniform1(_uHasTex, tex != 0 ? 1f : 0f);
                }
            }
            gl.Uniform1(_uTerrain, terrain ? 1f : 0f);
            if (!terrain)
            {
                tex = mat != null && !string.IsNullOrEmpty(mat.BaseMap) ? TextureFor(map, mat.BaseMap) : 0;
                if (tex != 0)
                {
                    gl.BindTexture(tex);
                    gl.Uniform4(_uUvXform, mat!.ScaleX, mat.ScaleY, mat.OffsetX, mat.OffsetY);
                    gl.Uniform1(_uHasTex, 1f);
                }
                else gl.Uniform1(_uHasTex, 0f);
            }
            bool flat = !terrain && tex == 0 && mat != null && mat.Color.W > 0;
            if (flat) gl.Uniform4(_uColor, mat!.Color.X, mat.Color.Y, mat.Color.Z, mat.Color.W);
            gl.Uniform1(_uAlphaTest, mat?.AlphaTest == true && tex != 0 && !terrain ? 1f : 0f);
            float blend = 0f;
            if (translucent)
            {
                switch (mat!.Blend)
                {
                    case "additive":
                        blend = 2f; gl.BlendFunc(Gl.ONE, Gl.ONE);
                        if (tex == 0 && !flat) gl.Uniform4(_uColor, 0.07f, 0.08f, 0.09f, 1f); // untextured glass: a faint sheen
                        break;
                    case "multiply":
                        blend = 3f; gl.BlendFunc(Gl.DST_COLOR, Gl.ZERO);
                        break;
                    case "double_multiply":
                        blend = 3f; gl.BlendFunc(Gl.DST_COLOR, Gl.SRC_COLOR);
                        break;
                    case "pre_multiplied_alpha":
                        blend = 1f; gl.BlendFunc(Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA);
                        if (tex == 0 && !flat) gl.Uniform4(_uColor, color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 0.3f);
                        break;
                    default: // alpha_blend
                        blend = 1f; gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
                        if (tex == 0 && !flat) gl.Uniform4(_uColor, color.X, color.Y, color.Z, 0.3f);
                        break;
                }
            }
            gl.Uniform1(_uBlend, blend);
            gl.DrawArrays(Gl.TRIANGLES, g.Start, g.Count);
            if ((translucent && tex == 0) || flat) gl.Uniform4(_uColor, color.X, color.Y, color.Z, color.W);
        }
        gl.Uniform1(_uBlend, 0f);
        gl.Uniform1(_uTerrain, 0f);
        gl.Uniform1(_uAlphaTest, 0f);
    }

    // ---- rendering -----------------------------------------------------------------------------

    private Vector3 Forward => new(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch));
    private Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitZ));
    private Vector3 Up => Vector3.Cross(Right, Forward);

    private (int w, int h) PixelSize()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return (Math.Max(1, (int)(Bounds.Width * scale)), Math.Max(1, (int)(Bounds.Height * scale)));
    }

    private Matrix4x4 ViewProj(int w, int h)
    {
        var view = Matrix4x4.CreateLookAt(_eye, _eye + Forward, Vector3.UnitZ);
        float near = 0.05f, far = 3000f, f = 1f / MathF.Tan(Fov / 2f), aspect = (float)w / h;
        var proj = new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
        return view * proj;
    }

    protected override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        var gl = _gl;
        if (gl == null) return;
        var map = Map;
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        bool animate = Fly(dt);

        var (w, h) = PixelSize();
        gl.BindFramebuffer(fb);
        gl.Viewport(0, 0, w, h);
        gl.ClearColor(0.11f, 0.115f, 0.13f, 1f);
        gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        if (map == null) return;

        if (map.GeometryVersion != _bspVersion)
        {
            _bspVersion = map.GeometryVersion;
            Free(_bsp);
            _bsp = map.Bsp != null && map.Bsp.TriangleCount > 0 ? UploadMesh(map.Bsp) : null;
        }
        if (map.TexturesVersion != _texturesVersion)
        {
            // New images may be ready: forget the bitmaps that had none so they are looked up again.
            _texturesVersion = map.TexturesVersion;
            foreach (var key in _textures.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) _textures.Remove(key);
        }
        if (!_cameraPlaced) PlaceCamera(map);

        gl.Enable(Gl.DEPTH_TEST);
        gl.DepthFunc(Gl.LEQUAL);
        gl.DepthMask(true);
        gl.Disable(Gl.CULL_FACE);
        gl.UseProgram(_program);
        gl.UniformMatrix(_uViewProj, ViewProj(w, h));
        gl.Uniform3(_uEye, _eye);
        gl.Uniform1(_uLit, 1f);
        bool wireframe = map.Shading == ViewShading.Wireframe;
        gl.Uniform1(_uMode, map.Shading switch { ViewShading.Wireframe => 0f, ViewShading.Solid => 1f, _ => 2f });
        gl.Uniform1(_uWire, map.WireOverlay && !wireframe ? 1f : 0f);
        gl.UniformInt(_uTex, 0);
        for (int l = 0; l < 4; l++) gl.UniformInt(_uLayer[l], 1 + l);
        gl.Uniform1(_uTerrain, 0f);
        gl.Uniform1(_uHasTex, 0f);
        gl.Uniform1(_uTint, 0f);
        gl.Uniform4(_uUvXform, 1f, 1f, 0f, 0f);
        gl.Uniform1(_uBlend, 0f);
        gl.Uniform1(_uAlphaTest, 0f);
        if (wireframe)
        {
            // Edges only, blended for smooth lines; nothing hides what is behind (like Blender's wireframe).
            gl.Enable(Gl.BLEND);
            gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            gl.DepthMask(false);
        }

        PlacementItem? selected = map.Selected;
        var bspColor = wireframe ? new Vector4(0.45f, 0.48f, 0.53f, 1f) : new Vector4(0.62f, 0.64f, 0.68f, 1f);
        DrawScene(gl, map, bspColor, translucentPass: false);
        if (map.Shading == ViewShading.Textured)
        {
            // Glass, decals and lights over the opaque scene: tested against depth but not writing it.
            gl.Enable(Gl.BLEND);
            gl.DepthMask(false);
            DrawScene(gl, map, bspColor, translucentPass: true);
            gl.DepthMask(true);
            gl.Disable(Gl.BLEND);
        }

        if (wireframe)
        {
            gl.Disable(Gl.BLEND);
            gl.DepthMask(true);
        }

        DrawZones(gl, map);

        if (selected != null)
        {
            // Outlines and the move/turn handles on top of everything, so the selection is never lost behind walls.
            gl.Disable(Gl.DEPTH_TEST);
            gl.Uniform1(_uLit, 0f);
            gl.Uniform4(_uColor, 1f, 0.85f, 0.35f, 1f);
            gl.BindVertexArray(_edgeVao);
            foreach (var item in map.Selection)
            {
                gl.UniformMatrix(_uModel, item.OutlineMatrix);
                gl.DrawArrays(Gl.LINES, 0, 24);
            }
            gl.BindVertexArray(_gizmoVao);
            gl.UniformMatrix(_uModel, Matrix4x4.CreateScale(GizmoScale(selected)) * Matrix4x4.CreateTranslation(selected.Position));
            var colors = new[] { new Vector3(0.95f, 0.3f, 0.3f), new Vector3(0.35f, 0.85f, 0.35f), new Vector3(0.35f, 0.55f, 1f), new Vector3(1f, 0.8f, 0.3f) };
            for (int part = 0; part < 4; part++)
            {
                var c = colors[part];
                float k = part == _hoverPart || (part < 3 && _drag == Drag.Axis && _axis == Axes[part]) || (part == 3 && _drag == Drag.Turn) ? 1.25f : 0.85f;
                gl.Uniform4(_uColor, Math.Min(1, c.X * k), Math.Min(1, c.Y * k), Math.Min(1, c.Z * k), 1f);
                if (part < 3) gl.DrawArrays(Gl.LINES, part * 2, 2);
                else gl.DrawArrays(Gl.LINES, 6, RingSegments * 2);
            }
            gl.Enable(Gl.DEPTH_TEST);
        }
        gl.BindVertexArray(0);
        SaveCapture(gl, w, h);

        if (animate) RequestNextFrameRendering();
    }

    /// <summary>
    /// Objective zones (multiplayer boundaries: hills, territories, capture and kill zones) as wire shapes:
    /// faint where they are, bright for the selection (drawn over walls so they can be resized from anywhere).
    /// </summary>
    private void DrawZones(Gl gl, MapEditorViewModel map)
    {
        var zones = map.VisibleItems.Where(i => i.HasZone).ToList();
        foreach (var s in map.Selection) if (s.HasZone && !zones.Contains(s)) zones.Add(s);
        if (zones.Count == 0) return;
        gl.Uniform1(_uLit, 0f);
        gl.Enable(Gl.BLEND);
        gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        gl.DepthMask(false);
        foreach (var z in zones)
        {
            bool sel = map.Selection.Contains(z);
            if (sel) gl.Disable(Gl.DEPTH_TEST);
            gl.Uniform4(_uColor, 0.3f, 0.85f, 1f, sel ? 0.95f : 0.45f);
            var pose = z.PoseMatrix;
            var size = z.ZoneSize;
            float r = Math.Max(size.X, 0.05f);
            float up = size.Z, down = size.W;
            const float ring = 0.75f; // the gizmo ring's radius
            switch (z.ZoneShape)
            {
                case 1: // sphere: three great circles
                    gl.BindVertexArray(_gizmoVao);
                    var s = Matrix4x4.CreateScale(r / ring);
                    foreach (var rot in new[] { Matrix4x4.Identity, Matrix4x4.CreateRotationX(MathF.PI / 2), Matrix4x4.CreateRotationY(MathF.PI / 2) })
                    {
                        gl.UniformMatrix(_uModel, s * rot * pose);
                        gl.DrawArrays(Gl.LINES, 6, RingSegments * 2);
                    }
                    break;
                case 2: // cylinder: top and bottom circles, four sides
                    gl.BindVertexArray(_gizmoVao);
                    foreach (float h in new[] { up, -down, 0f })
                    {
                        gl.UniformMatrix(_uModel, Matrix4x4.CreateScale(r / ring, r / ring, 1) * Matrix4x4.CreateTranslation(0, 0, h) * pose);
                        gl.DrawArrays(Gl.LINES, 6, RingSegments * 2);
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        float a = k * MathF.PI / 2;
                        // the gizmo's z axis line (0..1) stretched from the bottom to the top
                        gl.UniformMatrix(_uModel, Matrix4x4.CreateScale(1, 1, Math.Max(up + down, 0.05f)) *
                                                  Matrix4x4.CreateTranslation(MathF.Cos(a) * r, MathF.Sin(a) * r, -down) * pose);
                        gl.DrawArrays(Gl.LINES, 4, 2);
                    }
                    break;
                case 3: // box: width along x, length along y
                    gl.BindVertexArray(_edgeVao);
                    float hw = Math.Max(size.X, 0.05f) / 2, hl = Math.Max(size.Y, 0.05f) / 2, hh = Math.Max(up + down, 0.05f) / 2;
                    gl.UniformMatrix(_uModel, Matrix4x4.CreateScale(hw, hl, hh) * Matrix4x4.CreateTranslation(0, 0, (up - down) / 2) * pose);
                    gl.DrawArrays(Gl.LINES, 0, 24);
                    break;
            }
            if (sel) gl.Enable(Gl.DEPTH_TEST);
        }
        gl.DepthMask(true);
        gl.Disable(Gl.BLEND);
        gl.BindVertexArray(0);
    }

    private void PlaceCamera(MapEditorViewModel map)
    {
        Vector3 center;
        float size;
        // Frame the objects (the playable space); level geometry often includes a huge sky.
        // Frame where players are: respawn points if the map has them, else the middle half of all objects.
        // (Maps keep hidden areas and sky props far away; a plain bounding box would shrink the level to a dot.)
        var spawns = map.Items.Where(i => i.Tag.Contains("respawn_point", StringComparison.OrdinalIgnoreCase)).ToList();
        var basis = spawns.Count >= 3 ? spawns : map.Items.ToList();
        if (basis.Count > 0)
        {
            float P(Func<PlacementItem, float> axis, double q)
            {
                var v = basis.Select(axis).OrderBy(x => x).ToList();
                return v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)];
            }
            double lo = spawns.Count >= 3 ? 0.0 : 0.25, hi = spawns.Count >= 3 ? 1.0 : 0.75;
            var min = new Vector3(P(i => i.Position.X, lo), P(i => i.Position.Y, lo), P(i => i.Position.Z, lo));
            var max = new Vector3(P(i => i.Position.X, hi), P(i => i.Position.Y, hi), P(i => i.Position.Z, hi));
            center = (min + max) / 2; size = Math.Max((max - min).Length() * 1.3f, 15f);
        }
        else if (map.Bsp != null) { center = map.Bsp.Center; size = (map.Bsp.Max - map.Bsp.Min).Length(); }
        else return;
        _yaw = -2.3f; _pitch = -0.5f;
        _eye = center - Forward * Math.Max(size * 0.6f, 10f);
        _cameraPlaced = true;
    }

    /// <summary>Keyboard flying; returns true while a movement key is held (keep animating).</summary>
    private bool Fly(float dt)
    {
        if (_keys.Count == 0) return false;
        float speed = (_keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift) ? 40f : 10f) * dt;
        var move = Vector3.Zero;
        var keys = AppSettings.Current;
        bool Held(string action) { var k = keys.HoldKey(action); return k != Key.None && _keys.Contains(k); }
        bool any = false;
        if (Held("forward")) { move += Forward; any = true; }
        if (Held("back")) { move -= Forward; any = true; }
        if (Held("right")) { move += Right; any = true; }
        if (Held("left")) { move -= Right; any = true; }
        if (Held("up")) { move += Vector3.UnitZ; any = true; }
        if (Held("down")) { move -= Vector3.UnitZ; any = true; }
        if (move != Vector3.Zero) _eye += Vector3.Normalize(move) * speed;
        return any;
    }

    // ---- move / turn handles ------------------------------------------------------------------------

    private static readonly Vector3[] Axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };

    /// <summary>Handles keep the same size on screen: about 15% of the distance to the camera.</summary>
    private float GizmoScale(PlacementItem item) => Math.Max(0.2f, Vector3.Distance(_eye, item.Position) * 0.15f);

    private Point? ToScreen(Vector3 p)
    {
        var vp = ViewProj((int)Math.Max(1, Bounds.Width), (int)Math.Max(1, Bounds.Height));
        var c = Vector4.Transform(new Vector4(p, 1), vp);
        if (c.W <= 0.001f) return null;
        return new Point((c.X / c.W + 1) * 0.5 * Bounds.Width, (1 - c.Y / c.W) * 0.5 * Bounds.Height);
    }

    private static double SegmentDistance(Point p, Point a, Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double len = vx * vx + vy * vy;
        double t = len < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len, 0, 1);
        double dx = a.X + t * vx - p.X, dy = a.Y + t * vy - p.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Which handle is under the mouse: 0/1/2 = x/y/z arrow, 3 = turn ring, -1 = none.</summary>
    private int HandleAt(Point mouse)
    {
        var sel = Map?.Selected;
        if (sel == null) return -1;
        float s = GizmoScale(sel);
        var origin = ToScreen(sel.Position);
        if (origin == null) return -1;
        int best = -1;
        double bestDistance = 8;
        for (int a = 0; a < 3; a++)
        {
            var tip = ToScreen(sel.Position + Axes[a] * s);
            if (tip == null) continue;
            double dist = SegmentDistance(mouse, origin.Value, tip.Value);
            if (dist < bestDistance) { bestDistance = dist; best = a; }
        }
        Point? prev = null;
        for (int i = 0; i <= RingSegments; i++)
        {
            float t = i * MathF.Tau / RingSegments;
            var q = ToScreen(sel.Position + new Vector3(MathF.Cos(t), MathF.Sin(t), 0) * (s * 0.75f));
            if (q != null && prev != null)
            {
                double dist = SegmentDistance(mouse, prev.Value, q.Value);
                if (dist < bestDistance) { bestDistance = dist; best = 3; }
            }
            prev = q;
        }
        return best;
    }

    /// <summary>Parameter t of the point on the line origin + t·axis closest to the ray (o, d).</summary>
    public static float ClosestOnAxis(Vector3 origin, Vector3 axis, Vector3 o, Vector3 d)
    {
        Vector3 w = origin - o;
        float a = Vector3.Dot(axis, axis), b = Vector3.Dot(axis, d), c = Vector3.Dot(d, d);
        float dd = Vector3.Dot(axis, w), e = Vector3.Dot(d, w);
        float denom = a * c - b * b;
        return MathF.Abs(denom) < 1e-6f ? 0 : (b * e - c * dd) / denom;
    }

    private float TurnAngle(Vector3 center, Vector3 o, Vector3 d)
    {
        var hit = HitPlane(o, d, center, Vector3.UnitZ) ?? center + Vector3.UnitX;
        return MathF.Atan2(hit.Y - center.Y, hit.X - center.X) * 180f / MathF.PI;
    }

    private void RememberSelection(MapEditorViewModel map)
    {
        _startPositions = map.Selection.ToDictionary(i => i, i => i.Position);
        _startYaws = map.Selection.ToDictionary(i => i, i => i.Yaw);
    }

    // ---- picking ---------------------------------------------------------------------------------

    private (Vector3 origin, Vector3 dir) Ray(Point p)
    {
        float x = (float)(2 * p.X / Math.Max(1, Bounds.Width) - 1);
        float y = (float)(1 - 2 * p.Y / Math.Max(1, Bounds.Height));
        float t = MathF.Tan(Fov / 2f), aspect = (float)(Bounds.Width / Math.Max(1, Bounds.Height));
        var dir = Vector3.Normalize(Forward + Right * (x * t * aspect) + Up * (y * t));
        return (_eye, dir);
    }

    private PlacementItem? Pick(Vector3 o, Vector3 d)
    {
        var map = Map;
        if (map == null) return null;
        PlacementItem? best = null;
        float bestT = float.MaxValue;
        foreach (var item in map.VisibleItems)
        {
            Vector3 c = item.Center;
            float r = item.PickRadius;
            Vector3 oc = o - c;
            float b = Vector3.Dot(oc, d);
            float disc = b * b - (oc.LengthSquared() - r * r);
            if (disc < 0) continue;
            float t = -b - MathF.Sqrt(disc);
            if (t < 0) t = -b + MathF.Sqrt(disc);
            if (t >= 0 && t < bestT) { bestT = t; best = item; }
        }
        return best;
    }

    /// <summary>Nearest hit on the level geometry (Möller–Trumbore), for placing new objects.</summary>
    private Vector3? HitGeometry(Vector3 o, Vector3 d)
    {
        var mesh = Map?.Bsp;
        if (mesh == null) return null;
        float best = float.MaxValue;
        var p = mesh.Positions;
        var idx = mesh.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = new Vector3(p[idx[i] * 3], p[idx[i] * 3 + 1], p[idx[i] * 3 + 2]);
            var b = new Vector3(p[idx[i + 1] * 3], p[idx[i + 1] * 3 + 1], p[idx[i + 1] * 3 + 2]);
            var c = new Vector3(p[idx[i + 2] * 3], p[idx[i + 2] * 3 + 1], p[idx[i + 2] * 3 + 2]);
            Vector3 e1 = b - a, e2 = c - a, pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-8f) continue;
            float inv = 1f / det;
            Vector3 tv = o - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0 || u > 1) continue;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, qv) * inv;
            if (v < 0 || u + v > 1) continue;
            float t = Vector3.Dot(e2, qv) * inv;
            if (t > 0.01f && t < best) best = t;
        }
        return best < float.MaxValue ? o + d * best : null;
    }

    /// <summary>Closest hit on the level or on any visible object's model (Forge maps are mostly objects).</summary>
    private Vector3? HitScene(Vector3 o, Vector3 d, PlacementItem? ignore = null)
    {
        var map = Map;
        Vector3? best = HitGeometry(o, d);
        float bestT = best.HasValue ? Vector3.Distance(o, best.Value) : float.MaxValue;
        if (map == null) return best;
        foreach (var item in map.VisibleItems)
        {
            if (item == ignore || item.Model is not { TriangleCount: > 0 } model) continue;
            // Cheap reject: the ray must pass near the object's bounding sphere.
            Vector3 c = item.Center;
            float along = Vector3.Dot(c - o, d);
            float r = item.PickRadius;
            if (along < -r || along - r > bestT) continue;
            if ((o + d * along - c).LengthSquared() > r * r) continue;
            if (!Matrix4x4.Invert(item.ModelMatrix, out var inv)) continue;
            Vector3 lo = Vector3.Transform(o, inv);
            Vector3 ld = Vector3.Normalize(Vector3.TransformNormal(d, inv));
            float? t = HitMesh(model, lo, ld);
            if (t is not { } tl) continue;
            Vector3 world = Vector3.Transform(lo + ld * tl, item.ModelMatrix);
            float tw = Vector3.Distance(o, world);
            if (tw < bestT) { bestT = tw; best = world; }
        }
        return best;
    }

    /// <summary>
    /// Where a new object goes: on the surface at the center of the view if it is reasonably close,
    /// otherwise a few units in front of the camera, dropped onto what is below.
    /// </summary>
    private Vector3 PlacementPoint()
    {
        Vector3 fwd = Forward;
        var hit = HitScene(_eye, fwd);
        if (hit is { } h && Vector3.Distance(h, _eye) < 25f) return h;
        Vector3 ahead = _eye + fwd * 6f;
        var below = HitScene(ahead + new Vector3(0, 0, 0.5f), -Vector3.UnitZ);
        return below is { } b && ahead.Z - b.Z < 12f ? b : ahead;
    }

    /// <summary>
    /// Play from camera: the ground (level or object) straight below the camera, facing where the camera looks.
    /// With nothing below within 60 units the player starts at the camera and falls.
    /// </summary>
    /// <summary>Why the camera is a bad start: no floor under it, or no room to stand. Null when it looks fine.</summary>
    private string? PlayFromProblem()
    {
        if (Map?.Bsp == null) return null; // no level geometry loaded: nothing to check against
        var below = HitScene(_eye + new Vector3(0, 0, 0.2f), -Vector3.UnitZ);
        if (below is not { } b || _eye.Z - b.Z >= 60f)
            return "There is no floor under the camera: the player would fall (in a mission, the game then restarts from its last checkpoint over and over).";
        // A Spartan is about 0.7 units (7 ft) tall.
        var above = HitScene(b + new Vector3(0, 0, 0.15f), Vector3.UnitZ);
        if (above is { } a && a.Z - b.Z < 0.75f)
            return $"The space under the camera is only {(a.Z - b.Z) * 10:F0} ft high: the player would be stuck (or the camera is inside a wall or under the floor).";
        return null;
    }

    private (Vector3 Position, float FacingDegrees) PlayFromPoint()
    {
        var below = HitScene(_eye + new Vector3(0, 0, 0.2f), -Vector3.UnitZ);
        Vector3 p = below is { } b && _eye.Z - b.Z < 60f ? b + new Vector3(0, 0, 0.1f) : _eye - new Vector3(0, 0, 0.6f);
        return (p, _yaw * 180f / MathF.PI);
    }

    private static float? HitMesh(Mesh mesh, Vector3 o, Vector3 d)
    {
        float best = float.MaxValue;
        var p = mesh.Positions;
        var idx = mesh.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = new Vector3(p[idx[i] * 3], p[idx[i] * 3 + 1], p[idx[i] * 3 + 2]);
            var b = new Vector3(p[idx[i + 1] * 3], p[idx[i + 1] * 3 + 1], p[idx[i + 1] * 3 + 2]);
            var c = new Vector3(p[idx[i + 2] * 3], p[idx[i + 2] * 3 + 1], p[idx[i + 2] * 3 + 2]);
            Vector3 e1 = b - a, e2 = c - a, pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-8f) continue;
            float inv = 1f / det;
            Vector3 tv = o - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0 || u > 1) continue;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, qv) * inv;
            if (v < 0 || u + v > 1) continue;
            float t = Vector3.Dot(e2, qv) * inv;
            if (t > 0.01f && t < best) best = t;
        }
        return best < float.MaxValue ? best : null;
    }

    private static Vector3? HitPlane(Vector3 o, Vector3 d, Vector3 point, Vector3 normal)
    {
        float denom = Vector3.Dot(d, normal);
        if (MathF.Abs(denom) < 1e-5f) return null;
        float t = Vector3.Dot(point - o, normal) / denom;
        return t > 0 ? o + d * t : null;
    }

    // ---- input -------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _last = point.Position;
        var map = Map;
        if (point.Properties.IsRightButtonPressed) _drag = Drag.Look;
        else if (point.Properties.IsMiddleButtonPressed) _drag = Drag.Pan;
        else if (point.Properties.IsLeftButtonPressed && map != null)
        {
            var (o, d) = Ray(point.Position);
            int handle = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? -1 : HandleAt(point.Position);
            if (handle >= 0 && map.Selected != null)
            {
                var sel = map.Selected;
                RememberSelection(map);
                _moved = false;
                _dragItem = sel;
                if (handle < 3)
                {
                    _drag = Drag.Axis;
                    _axis = Axes[handle];
                    _axisOrigin = sel.Position;
                    _axisT0 = ClosestOnAxis(_axisOrigin, _axis, o, d);
                }
                else
                {
                    _drag = Drag.Turn;
                    _axisOrigin = sel.Position;
                    _turnAngle0 = TurnAngle(_axisOrigin, o, d);
                }
            }
            else
            {
                var hit = Pick(o, d);
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                _shiftToggle = null;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || (shift && hit != null && !map.IsSelected(hit)))
                {
                    // Ctrl+click or Shift+click: add the object to the selection (Ctrl+click on a selected one removes it).
                    if (hit != null) map.ToggleSelection(hit);
                }
                else
                {
                    // Shift+click on a selected object: removes it, unless the mouse moves (then it is a vertical drag).
                    if (shift && hit != null) _shiftToggle = hit;
                    if (hit == null || !map.IsSelected(hit)) map.Selected = hit;
                    else if (map.Selected != hit) map.MakePrimary(hit); // keep the group
                    if (hit != null)
                    {
                        _drag = Drag.Move;
                        _dragItem = hit;
                        _dragVertical = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                        _moved = false;
                        RememberSelection(map);
                        var plane = HitPlane(o, d, hit.Position, DragNormal(_dragVertical));
                        _grabOffset = plane.HasValue ? plane.Value - hit.Position : Vector3.Zero;
                    }
                    else
                    {
                        map.CursorPoint = HitScene(o, d);
                    }
                }
            }
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        RequestNextFrameRendering();
    }

    private Vector3 DragNormal(bool vertical)
    {
        if (!vertical) return Vector3.UnitZ;
        var n = new Vector3(Forward.X, Forward.Y, 0);
        return n.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(n);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None)
        {
            int hover = HandleAt(e.GetPosition(this));
            if (hover != _hoverPart)
            {
                _hoverPart = hover;
                Cursor = hover >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                RequestNextFrameRendering();
            }
            return;
        }
        var pos = e.GetPosition(this);
        double dx = pos.X - _last.X, dy = pos.Y - _last.Y;
        _last = pos;
        switch (_drag)
        {
            case Drag.Look:
                _yaw -= (float)dx * 0.005f;
                _pitch = Math.Clamp(_pitch - (float)dy * 0.005f, -1.55f, 1.55f);
                break;
            case Drag.Pan:
                _eye += (-Right * (float)dx + Up * (float)dy) * 0.05f;
                break;
            case Drag.Move when _dragItem != null && Map != null:
            {
                var (o, d) = Ray(pos);
                Vector3 start = _startPositions.TryGetValue(_dragItem, out var sp) ? sp : _dragItem.Position;
                var hit = HitPlane(o, d, start, DragNormal(_dragVertical));
                if (hit == null) break;
                Vector3 target = hit.Value - _grabOffset;
                Vector3 p = _dragVertical ? new Vector3(start.X, start.Y, target.Z) : new Vector3(target.X, target.Y, start.Z);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    p = new Vector3(MathF.Round(p.X * 4) / 4, MathF.Round(p.Y * 4) / 4, MathF.Round(p.Z * 4) / 4);
                Map.MoveSelectionLocal(_startPositions, p - start);
                _moved = true;
                break;
            }
            case Drag.Axis when Map != null:
            {
                var (o, d) = Ray(pos);
                float t = ClosestOnAxis(_axisOrigin, _axis, o, d) - _axisT0;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) t = MathF.Round(t * 4) / 4;
                Map.MoveSelectionLocal(_startPositions, _axis * t);
                _moved = true;
                break;
            }
            case Drag.Turn when Map != null:
            {
                var (o, d) = Ray(pos);
                float delta = TurnAngle(_axisOrigin, o, d) - _turnAngle0;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) delta = MathF.Round(delta / 15f) * 15f;
                Map.RotateSelectionLocal(_startYaws, delta);
                _moved = true;
                break;
            }
        }
        RequestNextFrameRendering();
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        bool edited = (_drag is Drag.Move or Drag.Axis or Drag.Turn) && _moved;
        if (!_moved && _shiftToggle != null && Map != null && Map.IsSelected(_shiftToggle)) Map.ToggleSelection(_shiftToggle);
        _shiftToggle = null;
        _drag = Drag.None;
        _dragItem = null;
        e.Pointer.Capture(null);
        if (edited && Map != null) await Map.CommitSelectionAsync();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _eye += Forward * (float)e.Delta.Y * 2.5f;
        e.Handled = true;
        RequestNextFrameRendering();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var map = Map;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shortcut = map?.ShortcutFor(e, PlacementPoint);
        if (shortcut != null)
        {
            e.Handled = true;
            await shortcut();
            RequestNextFrameRendering();
            return;
        }
        if (ctrl) return;
        _keys.Add(e.Key);
        _lastFrame = _clock.Elapsed.TotalSeconds;
        e.Handled = true;
        RequestNextFrameRendering();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keys.Remove(e.Key);
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _keys.Clear();
    }
}

/// <summary>Vertex data for the shapes the map view draws (Stride floats per vertex).</summary>
internal static class Shapes
{
    /// <summary>Floats per vertex: position (3), normal (3), uv (2), barycentric (3, for edges).</summary>
    public const int Stride = 11;

    private static void Vertex(List<float> list, Vector3 p, Vector3 n, float u = 0, float v = 0, Vector3 bary = default)
    {
        list.Add(p.X); list.Add(p.Y); list.Add(p.Z);
        list.Add(n.X); list.Add(n.Y); list.Add(n.Z);
        list.Add(u); list.Add(v);
        list.Add(bary.X); list.Add(bary.Y); list.Add(bary.Z);
    }

    public static float[] CubeTriangles()
    {
        var list = new List<float>(36 * Stride);
        void Face(Vector3 n, Vector3 u, Vector3 v)
        {
            Vector3 c = n;
            Vector3[] q = { c - u - v, c + u - v, c + u + v, c - u + v };
            // Barycentrics with the component that is 0 along the diagonal (0-2) raised to 1, so in wireframe
            // only the 4 quad edges show. Triangle 1: corners 0,1,2; triangle 2: corners 0,2,3.
            Vertex(list, q[0], n, 0, 0, new Vector3(1, 1, 0)); Vertex(list, q[1], n, 1, 0, new Vector3(0, 1, 0)); Vertex(list, q[2], n, 1, 1, new Vector3(0, 1, 1));
            Vertex(list, q[0], n, 0, 0, new Vector3(1, 0, 1)); Vertex(list, q[2], n, 1, 1, new Vector3(0, 1, 1)); Vertex(list, q[3], n, 0, 1, new Vector3(0, 0, 1));
        }
        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        return list.ToArray();
    }

    /// <summary>Three unit axis lines (x, y, z) then a ring of radius 0.75 in the horizontal plane, as line pairs.</summary>
    public static float[] Gizmo(int segments)
    {
        var list = new List<float>();
        void V(Vector3 p) => Vertex(list, p, Vector3.UnitZ);
        foreach (var a in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }) { V(Vector3.Zero); V(a); }
        for (int i = 0; i < segments; i++)
        {
            float t0 = i * MathF.Tau / segments, t1 = (i + 1) * MathF.Tau / segments;
            V(new Vector3(MathF.Cos(t0), MathF.Sin(t0), 0) * 0.75f);
            V(new Vector3(MathF.Cos(t1), MathF.Sin(t1), 0) * 0.75f);
        }
        return list.ToArray();
    }

    public static float[] CubeEdges()
    {
        var list = new List<float>(24 * Stride);
        var corners = new List<Vector3>();
        for (int i = 0; i < 8; i++) corners.Add(new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        for (int a = 0; a < 8; a++)
            for (int b = a + 1; b < 8; b++)
            {
                int diff = a ^ b;
                if (diff != 1 && diff != 2 && diff != 4) continue;
                foreach (var p in new[] { corners[a], corners[b] }) Vertex(list, p, Vector3.UnitZ);
            }
        return list.ToArray();
    }

    /// <summary>
    /// Expands an indexed mesh into triangles with one normal per face, its texture coordinates and
    /// barycentrics (vertex i of the expanded array is index i, so material groups keep their ranges).
    /// </summary>
    public static float[] FlatShaded(Mesh mesh)
    {
        var p = mesh.Positions;
        var uv = mesh.Uvs;
        bool hasUv = uv != null && uv.Length >= mesh.VertexCount * 2;
        var idx = mesh.Indices;
        int triangles = idx.Length / 3;
        var data = new float[triangles * 3 * Stride];
        int o = 0;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int ia = idx[i], ib = idx[i + 1], ic = idx[i + 2];
            var a = new Vector3(p[ia * 3], p[ia * 3 + 1], p[ia * 3 + 2]);
            var b = new Vector3(p[ib * 3], p[ib * 3 + 1], p[ib * 3 + 2]);
            var c = new Vector3(p[ic * 3], p[ic * 3 + 1], p[ic * 3 + 2]);
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            for (int k = 0; k < 3; k++)
            {
                int vi = k == 0 ? ia : k == 1 ? ib : ic;
                var v = k == 0 ? a : k == 1 ? b : c;
                data[o++] = v.X; data[o++] = v.Y; data[o++] = v.Z;
                data[o++] = n.X; data[o++] = n.Y; data[o++] = n.Z;
                data[o++] = hasUv ? uv![vi * 2] : 0;
                data[o++] = hasUv ? uv![vi * 2 + 1] : 0;
                data[o++] = k == 0 ? 1 : 0;
                data[o++] = k == 1 ? 1 : 0;
                data[o++] = k == 2 ? 1 : 0;
            }
        }
        return data;
    }
}
