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
/// Kalo's 3D map view: the level geometry (exported once by tool.exe, cached), every object placement as a
/// box sized by the object's bounding radius, a fly camera, click to select, drag to move.
///
/// Camera: right mouse + drag looks around, WASD/QE fly (Shift = faster), wheel moves forward, middle mouse pans.
/// Objects: click selects, drag moves along the ground (Shift: up/down, Ctrl: snap to 0.25),
/// R / Shift+R turns 15°, Ctrl+D duplicates, Delete removes, F frames the selection.
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
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec3 vNormal;
out vec3 vWorld;
void main() {
    vec4 w = uModel * vec4(aPosition, 1.0);
    vWorld = w.xyz;
    vNormal = mat3(uModel) * aNormal;
    gl_Position = uViewProj * w;
}";

    private const string FragmentShader = @"
in vec3 vNormal;
in vec3 vWorld;
uniform vec4 uColor;
uniform vec3 uEye;
uniform float uLit;
out vec4 fragColor;
void main() {
    vec3 c = uColor.rgb;
    if (uLit > 0.5) {
        vec3 n = normalize(vNormal);
        if (dot(n, uEye - vWorld) < 0.0) n = -n;
        float sun = max(dot(n, normalize(vec3(0.45, 0.3, 0.84))), 0.0);
        float head = abs(dot(n, normalize(uEye - vWorld)));
        // Floors warm, walls cool, ceilings dark: reads like a blockout.
        vec3 tint = mix(vec3(0.78, 0.82, 0.92), vec3(0.95, 0.9, 0.8), clamp(n.z, 0.0, 1.0));
        tint = mix(tint, vec3(0.55, 0.58, 0.65), clamp(-n.z, 0.0, 1.0));
        c = uColor.rgb * tint * (0.12 + 0.55 * sun + 0.33 * head);
        // Faint 1-unit grid on floors helps judge distances.
        vec2 g = abs(fract(vWorld.xy) - 0.5);
        float line = smoothstep(0.48, 0.5, max(g.x, g.y)) * clamp(n.z, 0.0, 1.0) * 0.12;
        c *= 1.0 - line;
        float fog = clamp(length(vWorld - uEye) / 600.0, 0.0, 0.55);
        c = mix(c, vec3(0.11, 0.115, 0.13), fog);
    }
    fragColor = vec4(c, uColor.a);
}";

    private Gl? _gl;
    private string? _glError;
    private uint _program;
    private int _uViewProj, _uModel, _uColor, _uEye, _uLit;
    private uint _bspVao, _bspVbo;
    private int _bspVertices, _bspVersion = -1;
    private uint _cubeVao, _cubeVbo, _edgeVao, _edgeVbo;
    private readonly Dictionary<Mesh, (uint vao, uint vbo, int count)> _models = new(ReferenceEqualityComparer.Instance);

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
        Vector3 at = HitGeometry(o, d) ?? o + d * 6f;
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
        }
        _subscribed = Map;
        if (_subscribed != null)
        {
            _subscribed.SceneChanged += OnSceneChanged;
            _subscribed.FocusRequested += OnFocusRequested;
            _subscribed.FrameAllRequested += OnFrameAll;
            _cameraPlaced = false;
        }
        RequestNextFrameRendering();
    }

    private void OnSceneChanged() => RequestNextFrameRendering();

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
        _gl.DeleteVertexArray(_bspVao); _gl.DeleteBuffer(_bspVbo);
        _gl.DeleteVertexArray(_cubeVao); _gl.DeleteBuffer(_cubeVbo);
        _gl.DeleteVertexArray(_edgeVao); _gl.DeleteBuffer(_edgeVbo);
        _gl.DeleteVertexArray(_gizmoVao); _gl.DeleteBuffer(_gizmoVbo);
        foreach (var m in _models.Values) { _gl.DeleteVertexArray(m.vao); _gl.DeleteBuffer(m.vbo); }
        _models.Clear();
        _gl.DeleteProgram(_program);
        _bspVao = _bspVbo = _cubeVao = _cubeVbo = _edgeVao = _edgeVbo = 0;
        _bspVersion = -1;
        _gl = null;
    }

    /// <summary>Uploads interleaved position+normal floats (6 per vertex).</summary>
    private (uint vao, uint vbo) Upload(float[] data)
    {
        var gl = _gl!;
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(Gl.ARRAY_BUFFER, vbo);
        gl.BufferData(Gl.ARRAY_BUFFER, data, Gl.STATIC_DRAW);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, 24, 0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, 24, 12);
        gl.BindVertexArray(0);
        return (vao, vbo);
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
            gl.DeleteVertexArray(_bspVao); gl.DeleteBuffer(_bspVbo);
            _bspVao = _bspVbo = 0; _bspVertices = 0;
            if (map.Bsp != null)
            {
                float[] data = Shapes.FlatShaded(map.Bsp);
                (_bspVao, _bspVbo) = Upload(data);
                _bspVertices = data.Length / 6;
            }
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

        if (_bspVertices > 0 && map.ShowGeometry)
        {
            gl.UniformMatrix(_uModel, Matrix4x4.Identity);
            gl.Uniform4(_uColor, 0.62f, 0.64f, 0.68f, 1f);
            gl.BindVertexArray(_bspVao);
            gl.DrawArrays(Gl.TRIANGLES, 0, _bspVertices);
        }

        PlacementItem? selected = map.Selected;
        foreach (var item in map.VisibleItems)
        {
            var c = item.Color;
            if (map.IsSelected(item)) gl.Uniform4(_uColor, 1f, 0.78f, 0.25f, 1f);
            else gl.Uniform4(_uColor, c.X, c.Y, c.Z, 1f);
            var model = item.Model;
            if (model != null && model.TriangleCount > 0)
            {
                if (!_models.TryGetValue(model, out var gpu))
                {
                    float[] data = Shapes.FlatShaded(model);
                    var (vao, vbo) = Upload(data);
                    gpu = (vao, vbo, data.Length / 6);
                    _models[model] = gpu;
                }
                gl.UniformMatrix(_uModel, item.ModelMatrix);
                gl.BindVertexArray(gpu.vao);
                gl.DrawArrays(Gl.TRIANGLES, 0, gpu.count);
            }
            else
            {
                gl.UniformMatrix(_uModel, item.BoxMatrix);
                gl.BindVertexArray(_cubeVao);
                gl.DrawArrays(Gl.TRIANGLES, 0, 36);
            }
        }

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

        if (animate) RequestNextFrameRendering();
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
        if (_keys.Contains(Key.W)) move += Forward;
        if (_keys.Contains(Key.S)) move -= Forward;
        if (_keys.Contains(Key.D)) move += Right;
        if (_keys.Contains(Key.A)) move -= Right;
        if (_keys.Contains(Key.E)) move += Vector3.UnitZ;
        if (_keys.Contains(Key.Q)) move -= Vector3.UnitZ;
        if (move != Vector3.Zero) _eye += Vector3.Normalize(move) * speed;
        return _keys.Overlaps(new[] { Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E });
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
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    if (hit != null) map.ToggleSelection(hit);
                }
                else
                {
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
                        map.CursorPoint = HitGeometry(o, d);
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
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.F when map?.Selected != null:
                OnFocusRequested(map.Selected.Center, map.Selected.PickRadius);
                e.Handled = true;
                return;
            case Key.Delete when map?.Selected != null:
                e.Handled = true;
                await map.DeleteSelectedCommand.ExecuteAsync(null);
                return;
            case Key.A when ctrl && map != null:
                foreach (var item in map.OutlinerItems.Where(i => !map.IsSelected(i))) map.ToggleSelection(item);
                e.Handled = true;
                return;
            case Key.D when ctrl && map?.Selected != null:
                e.Handled = true;
                await map.DuplicateSelectedCommand.ExecuteAsync(null);
                return;
            case Key.R when map?.Selected != null:
                e.Handled = true;
                await map.RotateSelectedAsync(shift ? -15f : 15f);
                return;
            case Key.Home:
                OnFrameAll();
                e.Handled = true;
                return;
            case Key.Escape when map != null:
                map.Selected = null;
                e.Handled = true;
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

/// <summary>Vertex data for the shapes the map view draws (6 floats per vertex: position, normal).</summary>
internal static class Shapes
{
    public static float[] CubeTriangles()
    {
        var list = new List<float>(36 * 6);
        void Face(Vector3 n, Vector3 u, Vector3 v)
        {
            Vector3 c = n;
            Vector3[] q = { c - u - v, c + u - v, c + u + v, c - u + v };
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
            {
                list.Add(q[i].X); list.Add(q[i].Y); list.Add(q[i].Z);
                list.Add(n.X); list.Add(n.Y); list.Add(n.Z);
            }
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
        void V(Vector3 p) { list.Add(p.X); list.Add(p.Y); list.Add(p.Z); list.Add(0); list.Add(0); list.Add(1); }
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
        var list = new List<float>(24 * 6);
        var corners = new List<Vector3>();
        for (int i = 0; i < 8; i++) corners.Add(new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        for (int a = 0; a < 8; a++)
            for (int b = a + 1; b < 8; b++)
            {
                int diff = a ^ b;
                if (diff != 1 && diff != 2 && diff != 4) continue;
                foreach (var p in new[] { corners[a], corners[b] })
                {
                    list.Add(p.X); list.Add(p.Y); list.Add(p.Z);
                    list.Add(0); list.Add(0); list.Add(1);
                }
            }
        return list.ToArray();
    }

    /// <summary>Expands an indexed mesh into triangles with one normal per face.</summary>
    public static float[] FlatShaded(Mesh mesh)
    {
        var p = mesh.Positions;
        var idx = mesh.Indices;
        var data = new float[idx.Length * 6];
        int o = 0;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = new Vector3(p[idx[i] * 3], p[idx[i] * 3 + 1], p[idx[i] * 3 + 2]);
            var b = new Vector3(p[idx[i + 1] * 3], p[idx[i + 1] * 3 + 1], p[idx[i + 1] * 3 + 2]);
            var c = new Vector3(p[idx[i + 2] * 3], p[idx[i + 2] * 3 + 1], p[idx[i + 2] * 3 + 2]);
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            foreach (var v in new[] { a, b, c })
            {
                data[o++] = v.X; data[o++] = v.Y; data[o++] = v.Z;
                data[o++] = n.X; data[o++] = n.Y; data[o++] = n.Z;
            }
        }
        return data;
    }
}
