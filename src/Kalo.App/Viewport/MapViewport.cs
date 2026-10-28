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
public sealed class MapViewport : OpenGlControlBase
{
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

    private enum Drag { None, Look, Pan, Move }
    private Drag _drag;
    private Point _last;
    private PlacementItem? _dragItem;
    private Vector3 _grabOffset;
    private bool _dragVertical, _moved;

    public MapViewport()
    {
        Focusable = true;
        ClipToBounds = true;
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
        }
        _subscribed = Map;
        if (_subscribed != null)
        {
            _subscribed.SceneChanged += OnSceneChanged;
            _subscribed.FocusRequested += OnFocusRequested;
            _cameraPlaced = false;
        }
        RequestNextFrameRendering();
    }

    private void OnSceneChanged() => RequestNextFrameRendering();

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
            if (item == selected) gl.Uniform4(_uColor, 1f, 0.78f, 0.25f, 1f);
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
            // Outline on top of everything so the selection is never lost behind walls.
            gl.Disable(Gl.DEPTH_TEST);
            gl.Uniform1(_uLit, 0f);
            gl.Uniform4(_uColor, 1f, 0.85f, 0.35f, 1f);
            gl.BindVertexArray(_edgeVao);
            gl.UniformMatrix(_uModel, selected.OutlineMatrix);
            gl.DrawArrays(Gl.LINES, 0, 24);
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
        // Percentiles, so a few far-away objects (sky props, kill volumes) do not shrink the map to a dot.
        if (map.Items.Count > 0)
        {
            float P(Func<PlacementItem, float> axis, double q)
            {
                var v = map.Items.Select(axis).OrderBy(x => x).ToList();
                return v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)];
            }
            var lo = new Vector3(P(i => i.Position.X, 0.1), P(i => i.Position.Y, 0.1), P(i => i.Position.Z, 0.1));
            var hi = new Vector3(P(i => i.Position.X, 0.9), P(i => i.Position.Y, 0.9), P(i => i.Position.Z, 0.9));
            center = (lo + hi) / 2; size = (hi - lo).Length() * 1.2f;
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
            var hit = Pick(o, d);
            map.Selected = hit;
            if (hit != null)
            {
                _drag = Drag.Move;
                _dragItem = hit;
                _dragVertical = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                _moved = false;
                var plane = HitPlane(o, d, hit.Position, DragNormal(_dragVertical));
                _grabOffset = plane.HasValue ? plane.Value - hit.Position : Vector3.Zero;
            }
            else
            {
                map.CursorPoint = HitGeometry(o, d);
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
        if (_drag == Drag.None) return;
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
                var hit = HitPlane(o, d, _dragItem.Position, DragNormal(_dragVertical));
                if (hit == null) break;
                Vector3 target = hit.Value - _grabOffset;
                Vector3 p = _dragItem.Position;
                p = _dragVertical ? new Vector3(p.X, p.Y, target.Z) : new Vector3(target.X, target.Y, p.Z);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    p = new Vector3(MathF.Round(p.X * 4) / 4, MathF.Round(p.Y * 4) / 4, MathF.Round(p.Z * 4) / 4);
                Map.MoveLocal(_dragItem, p);
                _moved = true;
                break;
            }
        }
        RequestNextFrameRendering();
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var item = _dragItem;
        bool moved = _drag == Drag.Move && _moved;
        _drag = Drag.None;
        _dragItem = null;
        e.Pointer.Capture(null);
        if (moved && item != null && Map != null) await Map.CommitMoveAsync(item);
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
            case Key.D when ctrl && map?.Selected != null:
                e.Handled = true;
                await map.DuplicateSelectedCommand.ExecuteAsync(null);
                return;
            case Key.R when map?.Selected != null:
                e.Handled = true;
                await map.RotateSelectedAsync(shift ? -15f : 15f);
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
