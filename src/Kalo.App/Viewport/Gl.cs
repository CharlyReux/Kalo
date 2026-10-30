using System.Runtime.InteropServices;
using System.Text;

namespace Kalo.App.Viewport;

/// <summary>
/// The few OpenGL (ES 3.0 / desktop 3.3) entry points the map view needs, loaded by name from the context
/// Avalonia gives us. Kept independent of Avalonia's own GL wrapper so the renderer only depends on GL itself.
/// </summary>
internal sealed unsafe class Gl
{
    public const int ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
    public const int FLOAT = 0x1406, UNSIGNED_INT = 0x1405, UNSIGNED_SHORT = 0x1403;
    public const int TRIANGLES = 0x0004, LINES = 0x0001;
    public const int DEPTH_TEST = 0x0B71, CULL_FACE = 0x0B44, BLEND = 0x0BE2;
    public const int COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    public const int VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const int VERSION = 0x1F02, LEQUAL = 0x0203, LESS = 0x0201, ALWAYS = 0x0207;
    public const int SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, ONE = 1, ZERO = 0, DST_COLOR = 0x0306, SRC_COLOR = 0x0300;
    public const int FRAMEBUFFER = 0x8D40;
    public const int TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0, RGBA = 0x1908, UNSIGNED_BYTE = 0x1401;
    public const int TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const int LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, REPEAT = 0x2901;
    public const int UNPACK_ALIGNMENT = 0x0CF5;

    private readonly delegate* unmanaged<int, uint*, void> _genBuffers;
    private readonly delegate* unmanaged<int, uint*, void> _deleteBuffers;
    private readonly delegate* unmanaged<int, uint, void> _bindBuffer;
    private readonly delegate* unmanaged<int, nint, void*, int, void> _bufferData;
    private readonly delegate* unmanaged<int, uint*, void> _genVertexArrays;
    private readonly delegate* unmanaged<int, uint*, void> _deleteVertexArrays;
    private readonly delegate* unmanaged<uint, void> _bindVertexArray;
    private readonly delegate* unmanaged<uint, int, int, byte, int, void*, void> _vertexAttribPointer;
    private readonly delegate* unmanaged<uint, void> _enableVertexAttribArray;
    private readonly delegate* unmanaged<int, uint> _createShader;
    private readonly delegate* unmanaged<uint, int, byte**, int*, void> _shaderSource;
    private readonly delegate* unmanaged<uint, void> _compileShader;
    private readonly delegate* unmanaged<uint, int, int*, void> _getShaderiv;
    private readonly delegate* unmanaged<uint, int, int*, byte*, void> _getShaderInfoLog;
    private readonly delegate* unmanaged<uint, void> _deleteShader;
    private readonly delegate* unmanaged<uint> _createProgram;
    private readonly delegate* unmanaged<uint, uint, void> _attachShader;
    private readonly delegate* unmanaged<uint, uint, byte*, void> _bindAttribLocation;
    private readonly delegate* unmanaged<uint, void> _linkProgram;
    private readonly delegate* unmanaged<uint, int, int*, void> _getProgramiv;
    private readonly delegate* unmanaged<uint, int, int*, byte*, void> _getProgramInfoLog;
    private readonly delegate* unmanaged<uint, void> _useProgram;
    private readonly delegate* unmanaged<uint, void> _deleteProgram;
    private readonly delegate* unmanaged<uint, byte*, int> _getUniformLocation;
    private readonly delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv;
    private readonly delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private readonly delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private readonly delegate* unmanaged<int, float, void> _uniform1f;
    private readonly delegate* unmanaged<int, int, int, void> _drawArrays;
    private readonly delegate* unmanaged<int, int, int, void*, void> _drawElements;
    private readonly delegate* unmanaged<int, void> _enable;
    private readonly delegate* unmanaged<int, void> _disable;
    private readonly delegate* unmanaged<int, void> _depthFunc;
    private readonly delegate* unmanaged<byte, void> _depthMask;
    private readonly delegate* unmanaged<int, int, void> _blendFunc;
    private readonly delegate* unmanaged<int, int, int, int, void> _viewport;
    private readonly delegate* unmanaged<float, float, float, float, void> _clearColor;
    private readonly delegate* unmanaged<int, void> _clear;
    private readonly delegate* unmanaged<int, byte*> _getString;
    private readonly delegate* unmanaged<int, uint, void> _bindFramebuffer;
    private readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;
    private readonly delegate* unmanaged<int, uint*, void> _genTextures;
    private readonly delegate* unmanaged<int, uint*, void> _deleteTextures;
    private readonly delegate* unmanaged<int, uint, void> _bindTexture;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
    private readonly delegate* unmanaged<int, int, int, void> _texParameteri;
    private readonly delegate* unmanaged<int, void> _generateMipmap;
    private readonly delegate* unmanaged<int, void> _activeTexture;
    private readonly delegate* unmanaged<int, int, void> _uniform1i;
    private readonly delegate* unmanaged<int, int, void> _pixelStorei;

    public bool IsEs { get; }
    public string Version { get; }

    public Gl(Func<string, IntPtr> load)
    {
        IntPtr L(string name)
        {
            IntPtr p = load(name);
            if (p == IntPtr.Zero) throw new InvalidOperationException("OpenGL function missing: " + name);
            return p;
        }
        _genBuffers = (delegate* unmanaged<int, uint*, void>)L("glGenBuffers");
        _deleteBuffers = (delegate* unmanaged<int, uint*, void>)L("glDeleteBuffers");
        _bindBuffer = (delegate* unmanaged<int, uint, void>)L("glBindBuffer");
        _bufferData = (delegate* unmanaged<int, nint, void*, int, void>)L("glBufferData");
        _genVertexArrays = (delegate* unmanaged<int, uint*, void>)L("glGenVertexArrays");
        _deleteVertexArrays = (delegate* unmanaged<int, uint*, void>)L("glDeleteVertexArrays");
        _bindVertexArray = (delegate* unmanaged<uint, void>)L("glBindVertexArray");
        _vertexAttribPointer = (delegate* unmanaged<uint, int, int, byte, int, void*, void>)L("glVertexAttribPointer");
        _enableVertexAttribArray = (delegate* unmanaged<uint, void>)L("glEnableVertexAttribArray");
        _createShader = (delegate* unmanaged<int, uint>)L("glCreateShader");
        _shaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)L("glShaderSource");
        _compileShader = (delegate* unmanaged<uint, void>)L("glCompileShader");
        _getShaderiv = (delegate* unmanaged<uint, int, int*, void>)L("glGetShaderiv");
        _getShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)L("glGetShaderInfoLog");
        _deleteShader = (delegate* unmanaged<uint, void>)L("glDeleteShader");
        _createProgram = (delegate* unmanaged<uint>)L("glCreateProgram");
        _attachShader = (delegate* unmanaged<uint, uint, void>)L("glAttachShader");
        _bindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)L("glBindAttribLocation");
        _linkProgram = (delegate* unmanaged<uint, void>)L("glLinkProgram");
        _getProgramiv = (delegate* unmanaged<uint, int, int*, void>)L("glGetProgramiv");
        _getProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)L("glGetProgramInfoLog");
        _useProgram = (delegate* unmanaged<uint, void>)L("glUseProgram");
        _deleteProgram = (delegate* unmanaged<uint, void>)L("glDeleteProgram");
        _getUniformLocation = (delegate* unmanaged<uint, byte*, int>)L("glGetUniformLocation");
        _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)L("glUniformMatrix4fv");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)L("glUniform4f");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>)L("glUniform3f");
        _uniform1f = (delegate* unmanaged<int, float, void>)L("glUniform1f");
        _drawArrays = (delegate* unmanaged<int, int, int, void>)L("glDrawArrays");
        _drawElements = (delegate* unmanaged<int, int, int, void*, void>)L("glDrawElements");
        _enable = (delegate* unmanaged<int, void>)L("glEnable");
        _disable = (delegate* unmanaged<int, void>)L("glDisable");
        _depthFunc = (delegate* unmanaged<int, void>)L("glDepthFunc");
        _depthMask = (delegate* unmanaged<byte, void>)L("glDepthMask");
        _blendFunc = (delegate* unmanaged<int, int, void>)L("glBlendFunc");
        _viewport = (delegate* unmanaged<int, int, int, int, void>)L("glViewport");
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)L("glClearColor");
        _clear = (delegate* unmanaged<int, void>)L("glClear");
        _getString = (delegate* unmanaged<int, byte*>)L("glGetString");
        _bindFramebuffer = (delegate* unmanaged<int, uint, void>)L("glBindFramebuffer");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)L("glReadPixels");
        _genTextures = (delegate* unmanaged<int, uint*, void>)L("glGenTextures");
        _deleteTextures = (delegate* unmanaged<int, uint*, void>)L("glDeleteTextures");
        _bindTexture = (delegate* unmanaged<int, uint, void>)L("glBindTexture");
        _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)L("glTexImage2D");
        _texParameteri = (delegate* unmanaged<int, int, int, void>)L("glTexParameteri");
        _generateMipmap = (delegate* unmanaged<int, void>)L("glGenerateMipmap");
        _activeTexture = (delegate* unmanaged<int, void>)L("glActiveTexture");
        _uniform1i = (delegate* unmanaged<int, int, void>)L("glUniform1i");
        _pixelStorei = (delegate* unmanaged<int, int, void>)L("glPixelStorei");
        Version = Marshal.PtrToStringAnsi((IntPtr)_getString(VERSION)) ?? "";
        IsEs = Version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
    }

    public uint GenBuffer() { uint b; _genBuffers(1, &b); return b; }
    public void DeleteBuffer(uint b) { if (b != 0) _deleteBuffers(1, &b); }
    public void BindBuffer(int target, uint b) => _bindBuffer(target, b);
    public void BufferData<T>(int target, T[] data, int usage) where T : unmanaged
    {
        fixed (T* p = data) _bufferData(target, (nint)(data.Length * sizeof(T)), p, usage);
    }
    public uint GenVertexArray() { uint a; _genVertexArrays(1, &a); return a; }
    public void DeleteVertexArray(uint a) { if (a != 0) _deleteVertexArrays(1, &a); }
    public void BindVertexArray(uint a) => _bindVertexArray(a);
    public void VertexAttribPointer(uint index, int size, int stride, int offset) => _vertexAttribPointer(index, size, FLOAT, 0, stride, (void*)offset);
    public void EnableVertexAttribArray(uint index) => _enableVertexAttribArray(index);
    public void UseProgram(uint p) => _useProgram(p);
    public void DeleteProgram(uint p) { if (p != 0) _deleteProgram(p); }
    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);
    public void DrawElements(int mode, int count, int type, int offset) => _drawElements(mode, count, type, (void*)offset);
    public void Enable(int cap) => _enable(cap);
    public void Disable(int cap) => _disable(cap);
    public void DepthFunc(int f) => _depthFunc(f);
    public void DepthMask(bool on) => _depthMask(on ? (byte)1 : (byte)0);
    public void BlendFunc(int s, int d) => _blendFunc(s, d);
    public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void Clear(int mask) => _clear(mask);
    public void BindFramebuffer(int fb) => _bindFramebuffer(FRAMEBUFFER, (uint)fb);

    /// <summary>RGBA bytes of the current framebuffer, bottom row first.</summary>
    public byte[] ReadPixels(int w, int h)
    {
        var data = new byte[w * h * 4];
        fixed (byte* p = data) _readPixels(0, 0, w, h, 0x1908 /* RGBA */, 0x1401 /* UNSIGNED_BYTE */, p);
        return data;
    }

    /// <summary>Uploads an RGBA image (rows top first; GL treats row 0 as v = 0) with mipmaps and repeat wrapping.</summary>
    public uint CreateTexture(int w, int h, byte[] rgba)
    {
        uint t;
        _genTextures(1, &t);
        _bindTexture(TEXTURE_2D, t);
        _pixelStorei(UNPACK_ALIGNMENT, 1);
        fixed (byte* p = rgba) _texImage2D(TEXTURE_2D, 0, RGBA, w, h, 0, RGBA, UNSIGNED_BYTE, p);
        _generateMipmap(TEXTURE_2D);
        _texParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, LINEAR_MIPMAP_LINEAR);
        _texParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, LINEAR);
        _texParameteri(TEXTURE_2D, TEXTURE_WRAP_S, REPEAT);
        _texParameteri(TEXTURE_2D, TEXTURE_WRAP_T, REPEAT);
        return t;
    }
    public void DeleteTexture(uint t) { if (t != 0) _deleteTextures(1, &t); }
    public void BindTexture(uint t, int unit = 0) { _activeTexture(TEXTURE0 + unit); _bindTexture(TEXTURE_2D, t); }
    public void UniformInt(int loc, int v) => _uniform1i(loc, v);

    public int Uniform(uint program, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return _getUniformLocation(program, p);
    }
    public void UniformMatrix(int loc, System.Numerics.Matrix4x4 m) => _uniformMatrix4fv(loc, 1, 0, &m.M11);
    public void Uniform4(int loc, float x, float y, float z, float w) => _uniform4f(loc, x, y, z, w);
    public void Uniform3(int loc, System.Numerics.Vector3 v) => _uniform3f(loc, v.X, v.Y, v.Z);
    public void Uniform1(int loc, float v) => _uniform1f(loc, v);

    /// <summary>Compiles and links a program; attributes 0 = position, 1 = normal, 2 = uv, 3 = barycentric.</summary>
    public uint BuildProgram(string vertexBody, string fragmentBody)
    {
        string header = IsEs ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        uint vs = Compile(VERTEX_SHADER, header + vertexBody);
        uint fs = Compile(FRAGMENT_SHADER, header + fragmentBody);
        uint program = _createProgram();
        _attachShader(program, vs);
        _attachShader(program, fs);
        BindAttrib(program, 0, "aPosition");
        BindAttrib(program, 1, "aNormal");
        BindAttrib(program, 2, "aUv");
        BindAttrib(program, 3, "aBary");
        _linkProgram(program);
        int ok;
        _getProgramiv(program, LINK_STATUS, &ok);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + Log(program, true));
        _deleteShader(vs);
        _deleteShader(fs);
        return program;
    }

    private void BindAttrib(uint program, uint index, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) _bindAttribLocation(program, index, p);
    }

    private uint Compile(int type, string source)
    {
        uint shader = _createShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* p = bytes)
        {
            byte* ptr = p;
            int len = bytes.Length;
            _shaderSource(shader, 1, &ptr, &len);
        }
        _compileShader(shader);
        int ok;
        _getShaderiv(shader, COMPILE_STATUS, &ok);
        if (ok == 0) throw new InvalidOperationException("Shader compile failed: " + Log(shader, false));
        return shader;
    }

    private string Log(uint obj, bool program)
    {
        var buffer = new byte[4096];
        int length;
        fixed (byte* p = buffer)
        {
            if (program) _getProgramInfoLog(obj, buffer.Length, &length, p);
            else _getShaderInfoLog(obj, buffer.Length, &length, p);
        }
        return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
    }
}
