using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's distant trees: the title's per-frame impostor of each tree model and
/// the camera-facing cards that draw it (VS 0092 / PS 0093).
/// </summary>
/// <remarks>
/// THE TITLE'S RULE (`sub_82208310`, `sub_822DAB20`, the capture and the batch draw):
/// - Every frame, each tree model with a placement in billboard range is rendered into a 256^2
///   impostor: its LOD model (else the main one) on its own tree materials, normalised by its
///   bound sphere, seen along the camera's horizontal forward at the camera's pitch, with the
///   frame's sun. The pixel stage's exposure c20.w is 1 / (0.25 B) for the capture, B the
///   billboard brightness (`sub_82A85E68`, below), so the impostor holds the unexposed lit colour.
/// - The cards: one record per placement, {guest-world position, world radius} (radius = |row 0|
///   x the model's bound radius, 0x822DB218..28), four vertices each. The stage sizes and turns
///   the card about the vertical, lifts it by radius x c8.z, fogs it per placement, and the pixel
///   stage multiplies the impostor by 0.25 B (c31.x), fades it in over [0.75, 0.875] F (c29) and
///   alpha-tests it. The full tree's own fade band is Engine_PropDrawRange's.
///
/// B = max over r, g, b of (lightmap factor x max(theme 192, theme 193) + the top light zone's
/// colour + the ambient light's), `sub_82A85E68` 0x82A85E68..0x82A86010: P193 and P192 are
/// T+16+0xC10 / +0xC00, max'd per lane (vmaxfp v2), w zeroed by the permute 0x820991A0, times
/// the 16 bytes at 0x834A4D40, plus *(zone stack top)+0xD0 when the stack 0x834A4D68..6C is not
/// empty, plus *(0x834A4D60)+0xD0, then the largest of x, y, z.
///
/// HERE the impostors share one atlas: every type is captured by one orthographic camera with
/// the main camera's orientation, each model turned so its file +Y runs along the camera's
/// horizontal forward (the title turns the sun into the model's frame instead; the picture is
/// the same) and placed in its own 256^2 cell. The capture draws on a constant row of its own
/// (the frame's, with the capture's exposure and eye), and on copies of the tree materials whose
/// screen-space shadow mask is white: the mask is the main view's, and the billboard applies its
/// own shadow-range term.
///
/// AN ATLAS PER VIEW. The impostor is the tree seen along the view's forward at its pitch, lit
/// from that side, so split screen's second view captures its own atlas (the same cells, its own
/// camera, models and capture row) and its cards sample it (<see cref="Engine_ClassicShaders.ViewSampler"/>).
/// It is built on the first split and stops drawing when the screen rejoins.
/// </remarks>
public partial class Engine_ClassicBillboards : Node
{
    /// <summary>Node metadata on the node whose model is the impostor: {F, cull}, the stated distances as drawn.</summary>
    public const string Meta = "classic_billboard";

    private const int Cell = 256;
    private const int Columns = 8;
    private const string VertexEntry = "VSHADER_TREEBILLBOARD";
    private const string PixelEntry = "PSHADER_TREEBILLBOARD";

    /// <summary>The capture world's camera stands this far behind the cells.</summary>
    private const float CaptureDepth = 50f;

    private static readonly StringName RowUniform = "xr_light_row";
    private static readonly StringName CullUniform = "xr_billboard_cull";
    private static readonly StringName ScreenShadow = "ps_g_ScreenShadowMap";
    private static readonly StringName TreeAmbient = "xr_tree_ambient";
    private static readonly StringName TreeStatedX = "xr_tree_stated_x";
    private static readonly StringName TreeStatedY = "xr_tree_stated_y";

    private sealed class Kind
    {
        public string Model = string.Empty;
        public Mesh Mesh = null!;
        /// <summary>Bound sphere in the file's axes {x, y, z, r}.</summary>
        public Vector4 Sphere;
        public float F;
        public float Cull;
        public readonly List<GeometryInstance3D> Sources = new();
        public bool Dirty = true;
        public int Placements;
        public int Signature;
        /// <summary>The capture's ambient and tree lanes, as the last card rebuild took them.</summary>
        public Vector4 CaptureAmbient, CaptureStatedX, CaptureStatedY;
        /// <summary>The source and stated ambient <see cref="CaptureAmbient"/> was taken from.</summary>
        public GeometryInstance3D? AmbientFrom;
        public Vector4 AmbientHeld;
        public MeshInstance3D? Draw;
        /// <summary>The kind's model in each view's atlas.</summary>
        public readonly Impostor?[] Impostors = new Impostor?[Engine_ClassicViews.Max];
        public int Row;
        public int CellIndex = -1;
    }

    /// <summary>A kind's model standing in one atlas, with the instance uniforms it was last given.</summary>
    private sealed class Impostor
    {
        public MeshInstance3D Node = null!;
        public int Row;
        public Vector4 Ambient = new(float.NaN, 0f, 0f, 0f);
        public Vector4 StatedX = new(float.NaN, 0f, 0f, 0f), StatedY;
    }

    /// <summary>One view's capture: its viewport, orthographic camera, mips, row and horizontal forward.</summary>
    private sealed class Atlas
    {
        public SubViewport Viewport = null!;
        public Camera3D Camera = null!;
        public Engine_ClassicImpostorMips Mips = null!;
        public int Row;
        public Vector3 Forward = new(0f, 0f, -1f);
    }

    private readonly Dictionary<string, Kind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Kind> _visible = new();
    private readonly List<Vector3> _points = new();
    private readonly List<float> _radii = new();
    private readonly Dictionary<ulong, ShaderMaterial> _captureMaterials = new();
    private readonly Atlas?[] _atlases = new Atlas?[Engine_ClassicViews.Max];
    private Shader? _shader;
    private ShaderMaterial? _cards;
    private bool _shaderTried;
    private int _rebuildCursor;

    private static readonly float[] FramePs = new float[Engine_ClassicConstants.Floats];
    private static readonly float[] FrameVs = new float[Engine_ClassicConstants.Floats];
    // The second view's card constants on split screen (FrameRows): its own view and projection rows.
    private static readonly float[] SecondPs = new float[Engine_ClassicConstants.Floats];
    private static readonly float[] SecondVs = new float[Engine_ClassicConstants.Floats];

    /// <summary>The live instance.</summary>
    public static Engine_ClassicBillboards? Instance { get; private set; }

    private static int _available = -1;

    /// <summary>
    /// Whether the classic renderer draws billboards: classic is on and the translated card stage
    /// carries the record's radius. The tree tiers end each model at F only when this holds, so
    /// stale shaders leave the trees as they were rather than cut off. Any thread.
    /// </summary>
    public static bool Available
    {
        get
        {
            if (!Engine_ClassicMaterial.Active)
            {
                return false;
            }
            if (_available < 0)
            {
                string? source = Engine_ClassicShaders.Source(VertexEntry);
                _available = source is not null && source.Contains("CUSTOM0.x", StringComparison.Ordinal) &&
                    Engine_ClassicShaders.Source(PixelEntry) is not null ? 1 : 0;
            }
            return _available == 1;
        }
    }

    /// <summary>Tree models with placements registered, and those captured on the last frame.</summary>
    public (int Kinds, int Visible, int Placements) Counts
    {
        get
        {
            int placements = 0;
            foreach (Kind kind in _kinds.Values)
            {
                placements += kind.Placements;
            }
            return (_kinds.Count, _visible.Count, placements);
        }
    }

    /// <summary>The billboard brightness B of the last frame.</summary>
    public static float Brightness { get; private set; } = 1f;

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicBillboards";
        _atlases[0] = MakeAtlas("ImpostorAtlas", "ImpostorCamera");
    }

    /// <summary>A capture viewport of its own world, its camera and the mips its cards sample.</summary>
    private Atlas MakeAtlas(string name, string cameraName)
    {
        var mips = new Engine_ClassicImpostorMips();
        var viewport = new SubViewport
        {
            Name = name,
            OwnWorld3D = true,
            TransparentBg = true,
            // The impostor holds unexposed linear colour; the card's stage scales it back.
            UseHdr2D = true,
            Msaa3D = Viewport.Msaa.Disabled,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Size = new Vector2I(Columns * Cell, Cell),
            // The impostor is the model's full detail, not the LOD Godot would pick for 256 pixels.
            MeshLodThreshold = 0f,
        };
        var camera = new Camera3D
        {
            Name = cameraName,
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 0.05f,
            Far = CaptureDepth * 2f,
            Current = true,
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
            // The mips are made inside the capture's render, ahead of the main view's depth pre-pass.
            Compositor = new Compositor
            {
                CompositorEffects = new Godot.Collections.Array<CompositorEffect> { mips },
            },
        };
        viewport.AddChild(camera);
        AddChild(viewport);
        return new Atlas { Viewport = viewport, Camera = camera, Mips = mips };
    }

    public override void _ExitTree()
    {
        foreach (Atlas? atlas in _atlases)
        {
            if (atlas is not null && atlas.Row != 0)
            {
                Engine_ClassicConstants.ReleaseRow(atlas.Row);
                atlas.Row = 0;
            }
        }
        foreach (Kind kind in _kinds.Values)
        {
            Engine_ClassicConstants.ReleaseRow(kind.Row);
        }
        _kinds.Clear();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>
    /// A tree node whose model is its placement's impostor, once its mesh stands (the streamer);
    /// a node without <see cref="Meta"/> is ignored. Main thread.
    /// </summary>
    public static void Register(GeometryInstance3D node, Mesh mesh)
    {
        if (Instance is not { } self || !Engine_ClassicMaterial.Active || !node.HasMeta(Meta) ||
            node.GetMeta(Meta).AsFloat32Array() is not { Length: >= 2 } stated ||
            !node.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta))
        {
            return;
        }
        string model = node.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString();
        if (!self._kinds.TryGetValue(model, out Kind? kind))
        {
            kind = new Kind { Model = model, Mesh = mesh, F = stated[0], Cull = stated[1] };
            kind.Sphere = SphereOf(mesh);
            self._kinds[model] = kind;
        }
        if (!kind.Sources.Contains(node))
        {
            kind.Sources.Add(node);
            kind.Dirty = true;
        }
    }

    /// <summary>The model's bound sphere in the file's axes; the mesh box's when the mesh predates the stamp.</summary>
    private static Vector4 SphereOf(Mesh mesh)
    {
        if (mesh.HasMeta(Engine_MdlMesh.BoundSphereMeta) &&
            mesh.GetMeta(Engine_MdlMesh.BoundSphereMeta).AsFloat32Array() is { Length: 4 } s && s[3] > 0f)
        {
            return new Vector4(s[0], s[1], s[2], s[3]);
        }
        Aabb box = mesh.GetAabb();
        Vector3 c = box.GetCenter();
        // Godot model (x, y, z) is the file's (x, -z, y).
        return new Vector4(c.X, -c.Z, c.Y, box.Size.Length() * 0.5f);
    }

    /// <summary>Captures and draws this frame's distant trees. Main thread, after the feed and the weather.</summary>
    public void Pump(Camera3D camera)
    {
        if (_atlases[0] is not { } atlas || !Engine_ClassicTheme.Live || !EnsureShader())
        {
            Hide();
            return;
        }
        Brightness = BillboardBrightness();
        Vector3 eye = camera.GlobalPosition;
        Follow(atlas, camera);
        Camera3D? secondCamera = Engine_ClassicViews.Count > 1 ? Engine_ClassicViews.Camera(1) : null;
        Atlas? second = SecondAtlas(secondCamera is not null);
        if (second is not null)
        {
            Follow(second, secondCamera!);
        }

        using (Refii.Core.Engine_FramePhases.Measure("classic.billboards.cards"))
        {
            Rebuild();
        }

        // The models with a placement in range: [0.75 F, cull).
        using var _ = Refii.Core.Engine_FramePhases.Measure("classic.billboards.capture");
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        _visible.Clear();
        foreach (Kind kind in _kinds.Values)
        {
            bool any = kind.Draw is not null && kind.Placements > 0 &&
                (InRange(kind, eye) || (Engine_ClassicViews.Count > 1 && InRange(kind, Engine_ClassicViews.Eye(1))));
            if (!any)
            {
                if (kind.Draw is not null)
                {
                    kind.Draw.Visible = false;
                }
                foreach (Impostor? impostor in kind.Impostors)
                {
                    if (impostor is not null)
                    {
                        impostor.Node.Visible = false;
                    }
                }
                kind.CellIndex = -1;
                continue;
            }
            kind.CellIndex = _visible.Count;
            _visible.Add(kind);
        }
        if (_visible.Count == 0)
        {
            atlas.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            if (second is not null)
            {
                second.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
            return;
        }

        Refii.Core.Engine_FramePhases.Lap("classic.billboards.range", ref mark);
        int rows = (_visible.Count + Columns - 1) / Columns;
        Stand(atlas, camera, rows);
        if (second is not null)
        {
            Stand(second, secondCamera!, rows);
        }
        FrameRows(camera);
        Refii.Core.Engine_FramePhases.Lap("classic.billboards.setup", ref mark);

        long placeTicks = 0, rowTicks = 0;
        foreach (Kind kind in _visible)
        {
            long from = System.Diagnostics.Stopwatch.GetTimestamp();
            Place(kind, rows, 0);
            if (second is not null)
            {
                Place(kind, rows, 1);
            }
            kind.Draw!.Visible = true;
            long middle = System.Diagnostics.Stopwatch.GetTimestamp();
            KindRow(kind, rows);
            placeTicks += middle - from;
            rowTicks += System.Diagnostics.Stopwatch.GetTimestamp() - middle;
        }
        double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Refii.Core.Engine_FramePhases.Record("classic.billboards.place", placeTicks * tickMs, false);
        Refii.Core.Engine_FramePhases.Record("classic.billboards.rows", rowTicks * tickMs, false);
    }

    /// <summary>The atlas's horizontal forward from its view's camera.</summary>
    private static void Follow(Atlas atlas, Camera3D camera)
    {
        Vector3 forward = -camera.GlobalBasis.Z;
        var flat = new Vector3(forward.X, 0f, forward.Z);
        if (flat.LengthSquared() > 1e-6f)
        {
            atlas.Forward = flat.Normalized();
        }
    }

    /// <summary>Sizes the atlas to the frame's cells, turns its camera to its view's and writes its row.</summary>
    private void Stand(Atlas atlas, Camera3D camera, int rows)
    {
        var size = new Vector2I(Columns * Cell, rows * Cell);
        if (atlas.Viewport.Size != size)
        {
            atlas.Viewport.Size = size;
        }
        atlas.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        atlas.Camera.GlobalTransform = new Transform3D(camera.GlobalBasis.Orthonormalized(), Vector3.Zero);
        atlas.Camera.Size = rows * 2f;
        CaptureRow(atlas);
    }

    /// <summary>
    /// The second view's atlas while the screen is split (built on the first split, its mips bound
    /// to the cards' second-view sampler); when it rejoins the atlas stops drawing and is kept.
    /// </summary>
    private Atlas? SecondAtlas(bool split)
    {
        Atlas? second = _atlases[1];
        if (!split)
        {
            if (second is not null && second.Viewport.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled)
            {
                second.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
            return null;
        }
        if (second is null)
        {
            second = _atlases[1] = MakeAtlas("ImpostorAtlasSecondView", "ImpostorCameraSecondView");
            _cards?.SetShaderParameter(PsDiffuseTextureSamplerSecondName, second.Mips.Texture);
        }
        return second;
    }

    private void Hide()
    {
        foreach (Atlas? atlas in _atlases)
        {
            if (atlas is not null)
            {
                atlas.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
        }
        foreach (Kind kind in _kinds.Values)
        {
            if (kind.Draw is not null)
            {
                kind.Draw.Visible = false;
            }
        }
    }

    private static bool InRange(Kind kind, Vector3 eye)
    {
        float near = 0.75f * kind.F;
        float nearSq = near * near, farSq = kind.Cull * kind.Cull;
        Aabb box = kind.Draw!.GetAabb();
        // Whole set out of reach or inside the near edge: nothing to capture.
        Vector3 nearest = eye.Clamp(box.Position, box.End);
        if ((nearest - eye).LengthSquared() >= farSq)
        {
            return false;
        }
        Vector3 c = box.GetCenter(), h = box.Size * 0.5f;
        Vector3 far = new(eye.X < c.X ? c.X + h.X : c.X - h.X, eye.Y < c.Y ? c.Y + h.Y : c.Y - h.Y,
            eye.Z < c.Z ? c.Z + h.Z : c.Z - h.Z);
        return (far - eye).LengthSquared() >= nearSq;
    }

    /// <summary>B, `sub_82A85E68` (see the class remarks): the billboards' and the fur's brightness.</summary>
    public static float BillboardBrightness()
    {
        float[] frame = Engine_ClassicFeed.RawFrame;
        Vector3 lightmap = new(frame[8], frame[9], frame[10]);
        Vector3 ambient = Engine_ClassicFeed.GlobalAmbient;
        Vector3 zone = Refii.Level.Engine_ClassicWeather.ZoneColour;
        var bounce = new Vector3(
            MathF.Max(Engine_ClassicTheme.Param(192, 0), Engine_ClassicTheme.Param(193, 0)),
            MathF.Max(Engine_ClassicTheme.Param(192, 1), Engine_ClassicTheme.Param(193, 1)),
            MathF.Max(Engine_ClassicTheme.Param(192, 2), Engine_ClassicTheme.Param(193, 2)));
        Vector3 sum = lightmap * bounce + zone + ambient;
        float b = MathF.Max(sum.X, MathF.Max(sum.Y, sum.Z));
        return float.IsFinite(b) && b > 1e-4f ? b : 1e-4f;
    }

    /// <summary>Re-reads one kind's placements a frame, and any kind whose sources changed.</summary>
    private void Rebuild()
    {
        if (_kinds.Count == 0)
        {
            return;
        }
        int i = 0, pick = _rebuildCursor++ % _kinds.Count;
        foreach (Kind kind in _kinds.Values)
        {
            if (kind.Dirty || i++ == pick)
            {
                kind.Dirty = false;
                BuildCards(kind);
            }
        }
    }

    /// <summary>
    /// The kind's cards: four vertices per standing placement, each carrying the placement's
    /// position (Godot axes; the stage reads it in the guest's) and its world radius in CUSTOM0.x.
    /// </summary>
    private void BuildCards(Kind kind)
    {
        List<Vector3> points = _points;
        List<float> radii = _radii;
        points.Clear();
        radii.Clear();
        float modelRadius = kind.Sphere.W;
        for (int s = kind.Sources.Count - 1; s >= 0; s--)
        {
            GeometryInstance3D source = kind.Sources[s];
            if (!GodotObject.IsInstanceValid(source) || !source.IsInsideTree())
            {
                kind.Sources.RemoveAt(s);
                continue;
            }
            if (!source.IsVisibleInTree())
            {
                continue;
            }
            Transform3D node = source.GlobalTransform;
            if (source is MultiMeshInstance3D batch && batch.Multimesh is { } multi)
            {
                Transform3D[]? stated = Refii.Level.Engine_PropShadow.StatedTransforms(multi);
                if (stated is null)
                {
                    continue;
                }
                foreach (Transform3D placement in stated)
                {
                    Add(node * placement);
                }
            }
            else
            {
                Add(node);
            }
        }

        void Add(Transform3D t)
        {
            float scale = t.Basis.Column0.Length();
            // A slot a live entity took over is collapsed to a point (Engine_StaticTwins).
            if (scale < 1e-4f || !float.IsFinite(scale))
            {
                return;
            }
            points.Add(t.Origin);
            radii.Add(scale * modelRadius);
        }

        kind.CaptureAmbient = CaptureAmbient(kind);
        (kind.CaptureStatedX, kind.CaptureStatedY) = CaptureTreeLanes(kind);

        // Unchanged placements keep the cards they have: the re-read is how a slot a live entity
        // took over (or gave back) is noticed, not a reason to re-upload the mesh.
        var signature = new HashCode();
        for (int i = 0; i < points.Count; i++)
        {
            signature.Add(points[i]);
            signature.Add(radii[i]);
        }
        int hash = signature.ToHashCode();
        if (kind.Draw is not null && points.Count == kind.Placements && hash == kind.Signature)
        {
            return;
        }
        kind.Signature = hash;
        kind.Placements = points.Count;
        if (points.Count == 0)
        {
            if (kind.Draw is not null)
            {
                kind.Draw.Visible = false;
            }
            return;
        }

        using var upload = Refii.Core.Engine_FramePhases.Measure("classic.billboards.upload");
        int n = points.Count;
        var vertices = new Vector3[n * 4];
        var custom = new float[n * 4 * 4];
        var indices = new int[n * 6];
        Aabb bounds = new(points[0], Vector3.Zero);
        float reach = 0f;
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                vertices[i * 4 + c] = points[i];
                custom[(i * 4 + c) * 4] = radii[i];
            }
            // The title's QUADLIST: (0, 1, 2) and (0, 2, 3).
            indices[i * 6] = i * 4;
            indices[i * 6 + 1] = i * 4 + 1;
            indices[i * 6 + 2] = i * 4 + 2;
            indices[i * 6 + 3] = i * 4;
            indices[i * 6 + 4] = i * 4 + 2;
            indices[i * 6 + 5] = i * 4 + 3;
            bounds = bounds.Expand(points[i]);
            reach = MathF.Max(reach, radii[i]);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Custom0] = custom;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
            Mesh.ArrayFormat.FormatCustom0 | (Mesh.ArrayFormat)((int)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        arrays.Dispose();
        // The stage lifts each card by up to its radius and spreads it by as much.
        mesh.CustomAabb = bounds.Grow(reach * 3f);

        if (kind.Draw is null)
        {
            _cards ??= CardMaterial();
            kind.Draw = new MeshInstance3D
            {
                Name = "Billboards",
                MaterialOverride = _cards,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = Engine_ClassicParticles.Layer,
                TopLevel = true,
                Visible = false,
            };
            AddChild(kind.Draw);
            kind.Row = Engine_ClassicConstants.AcquireRow();
            kind.Draw.SetInstanceShaderParameter(RowUniform, kind.Row);
            kind.Draw.SetInstanceShaderParameter(CullUniform, kind.Cull);
        }
        kind.Draw.Mesh = mesh;
        kind.Draw.GlobalTransform = Transform3D.Identity;
    }

    /// <summary>
    /// The cards' one material: every kind draws on it, its own constants in its row and its cut in
    /// an instance uniform, so the camera's two uniforms are set once a frame for all of them.
    /// </summary>
    private ShaderMaterial CardMaterial()
    {
        var material = new ShaderMaterial { Shader = _shader };
        material.SetShaderParameter(VsS16Name, Engine_ClassicAtmosphere.ExtinctionTexture);
        material.SetShaderParameter(VsS17Name, Engine_ClassicAtmosphere.InScatteringTexture);
        material.SetShaderParameter(XrUseGuestPositionName, true);
        material.SetShaderParameter(XrSpecConstantsName, 2u);
        material.SetShaderParameter(XrAlphaThresholdName, AlphaThreshold);
        // Set once: the mipped atlas (Engine_ClassicImpostorMips) keeps its object through
        // resizes, and a sampler set queues a uniform-set rebuild.
        // The second view's sampler takes the first's atlas until its own stands.
        material.SetShaderParameter(PsDiffuseTextureSamplerName, _atlases[0]!.Mips.Texture);
        material.SetShaderParameter(PsDiffuseTextureSamplerSecondName, (_atlases[1] ?? _atlases[0]!).Mips.Texture);
        return material;
    }

    /// <summary>
    /// The cards' cut. The batch enables the alpha test without stating its function or reference,
    /// which stay the frame defaults (ALPHAREF 128, ALPHAFUNC 7 = always), and alpha-to-mask with
    /// offsets 0xAA cuts instead (`sub_821C4A40`): here the midpoint, as the classic trees take it.
    /// </summary>
    private const float AlphaThreshold = 0.5f;

    private bool EnsureShader()
    {
        if (_shader is not null)
        {
            return true;
        }
        if (_shaderTried)
        {
            return false;
        }
        _shaderTried = true;
        // The tier's far cut, per placement: the cards stand from 0.75 F to the cull, and the
        // stage draws every card of the model whatever its distance.
        // The cards' camera uniforms belong to the view (split screen's second view reads its own).
        string defines =
            "instance uniform float xr_billboard_cull = 1.0e9;\n" +
            "#define XR_BEFORE_OUTPUT(c) if (distance(v_texcoord0.xyz, xr_const(xr_ps_constants, 19).xyz) >= xr_billboard_cull) { discard; }\n" +
            Engine_ClassicShaders.ViewParam("vs_g_CameraPos", false, Engine_ClassicRegisters.Vs.HostBillboardEye) + "\n" +
            Engine_ClassicShaders.ViewParam("vs_g_CameraZVector", false, Engine_ClassicRegisters.Vs.HostBillboardForward) + "\n" +
            Engine_ClassicShaders.ViewSampler("ps_g_DiffuseTextureSampler");
        _shader = Engine_ClassicShaders.Compose(VertexEntry, PixelEntry,
            "unshaded, depth_draw_opaque, cull_disabled, shadows_disabled, fog_disabled", 0, defines);
        // Before the translator carried the record's radius the stage read w = 1.
        if (_shader is null || !_shader.Code.Contains("CUSTOM0.x", StringComparison.Ordinal))
        {
            GD.Print("[classic] the tree billboard shaders predate the record's radius; billboards stay off until the shaders are compiled again");
            _shader = null;
            return false;
        }
        return true;
    }

    /// <summary>The capture's row: the frame's constants with the capture's exposure and eye.</summary>
    private void CaptureRow(Atlas atlas)
    {
        if (atlas.Row == 0)
        {
            atlas.Row = Engine_ClassicConstants.AcquireRow();
            if (atlas.Row == 0)
            {
                return;
            }
        }
        Engine_ClassicConstants.CopyFrame(true, FramePs);
        Engine_ClassicConstants.CopyFrame(false, FrameVs);
        // PS c20.w and VS c54.w = 1 / (0.25 B) (`sub_82A85AE0`): the impostor holds the unexposed colour.
        FramePs[Engine_ClassicRegisters.Ps.GlobalAmbientAndBrightness * 4 + 3] = 1f / (0.25f * Brightness);
        FrameVs[Engine_ClassicRegisters.Vs.GlobalAmbientAndBrightness * 4 + 3] = 1f / (0.25f * Brightness);
        Vector3 eye = ToGuest(atlas.Camera.GlobalPosition);
        SetVec(FramePs, Engine_ClassicRegisters.Ps.EyePosition, eye, 1f);
        SetVec(FrameVs, Engine_ClassicRegisters.Vs.EyePosition, eye, 1f);
        Engine_ClassicConstants.SetRowBlock(true, atlas.Row, 0, FramePs);
        Engine_ClassicConstants.SetRowBlock(false, atlas.Row, 0, FrameVs);
    }

    /// <summary>Stands the kind's impostor model in its cell of view <paramref name="view"/>'s atlas, facing its camera.</summary>
    private void Place(Kind kind, int rows, int view)
    {
        Atlas atlas = _atlases[view]!;
        if (kind.Impostors[view] is not { } impostor)
        {
            var node = new MeshInstance3D
            {
                Name = "Impostor",
                Mesh = kind.Mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            for (int s = 0; s < kind.Mesh.GetSurfaceCount(); s++)
            {
                if (kind.Mesh.SurfaceGetMaterial(s) is ShaderMaterial own && CaptureMaterial(own) is { } capture)
                {
                    node.SetSurfaceOverrideMaterial(s, capture);
                }
            }
            atlas.Camera.AddChild(node);
            node.TopLevel = true;
            impostor = kind.Impostors[view] = new Impostor { Node = node };
        }
        MeshInstance3D model = impostor.Node;
        model.Visible = true;
        // Instance uniforms only when they move: each set is a server call per kind per frame.
        if (impostor.Row != atlas.Row)
        {
            impostor.Row = atlas.Row;
            model.SetInstanceShaderParameter(RowUniform, atlas.Row);
        }
        // Taken when the kind's cards are rebuilt (BuildCards), never here: reading an instance
        // uniform back is a synchronous trip to the render thread, ~0.8 ms a kind a frame.
        if (kind.CaptureAmbient != impostor.Ambient)
        {
            impostor.Ambient = kind.CaptureAmbient;
            model.SetInstanceShaderParameter(TreeAmbient, impostor.Ambient);
        }
        if (kind.CaptureStatedX != impostor.StatedX || kind.CaptureStatedY != impostor.StatedY)
        {
            impostor.StatedX = kind.CaptureStatedX;
            impostor.StatedY = kind.CaptureStatedY;
            model.SetInstanceShaderParameter(TreeStatedX, impostor.StatedX);
            model.SetInstanceShaderParameter(TreeStatedY, impostor.StatedY);
        }

        Basis basis = atlas.Camera.GlobalBasis;
        int col = kind.CellIndex % Columns, row = kind.CellIndex / Columns;
        Vector3 centre = atlas.Camera.GlobalPosition - basis.Z * CaptureDepth
            + basis.X * (2f * col - (Columns - 1)) + basis.Y * ((rows - 1) - 2f * row);
        // The model's file +Y (Godot model -Z) along the camera's horizontal forward, scaled to
        // a unit sphere about the sphere's centre.
        float r = MathF.Max(kind.Sphere.W, 1e-3f);
        Basis turn = Basis.LookingAt(atlas.Forward, Vector3.Up).Scaled(Vector3.One / r);
        var sphereCentre = new Vector3(kind.Sphere.X, kind.Sphere.Z, -kind.Sphere.Y);
        model.GlobalTransform = new Transform3D(turn, centre - turn * sphereCentre);
    }

    /// <summary>
    /// The capture's VS c28: the PRT ambient of the placement the type's slot was made for
    /// (`sub_82221FE8` 0x822220C8 hands the item obj+0x274 / obj+0x1B0, the placement's own
    /// environment, and the capture keeps it; 0x82222008 swaps only the light binding to the
    /// global lights). Here the kind's first registered placement; a batch's copy carries its
    /// environment as its instance colour (xr_tree_ambient.w 2), which the capture node lacks.
    /// </summary>
    private static Vector4 CaptureAmbient(Kind kind)
    {
        foreach (GeometryInstance3D source in kind.Sources)
        {
            if (!GodotObject.IsInstanceValid(source) || !source.IsInsideTree())
            {
                continue;
            }
            if (!Engine_ClassicTransfer.TreeAmbientOf(source, out Vector4 ambient))
            {
                return Vector4.Zero;
            }
            if (ReferenceEquals(source, kind.AmbientFrom) && ambient == kind.AmbientHeld)
            {
                return kind.CaptureAmbient;
            }
            kind.AmbientFrom = source;
            kind.AmbientHeld = ambient;
            if (ambient.W > 1.5f)
            {
                // A batch's colours are written once, at its build (ApplyMultiMeshProbes).
                if (source is not MultiMeshInstance3D { Multimesh: { UseColors: true, InstanceCount: > 0 } batch })
                {
                    return Vector4.Zero;
                }
                Color first = batch.GetInstanceColor(0);
                return new Vector4(first.R * ambient.X, first.G * ambient.Y, first.B * ambient.Z, 1f);
            }
            return ambient;
        }
        return Vector4.Zero;
    }

    /// <summary>
    /// The capture's g_TreeConstants lanes: the title draws the slot's placement at the origin,
    /// unscaled (`sub_82218E58`: world rows identity), with that placement's own phase (c15.x,
    /// R+0x250) and wind in its axes (c17.xy = wind . axis / |axis|^2, `sub_8220B560`), so the
    /// impostor sways as the tree does. Here the model stands scaled to its cell at a point that
    /// turns with the camera, so the lanes its own matrix would give are stated instead from the
    /// kind's first placement: X = (x axis / |x|^2, phase), Y = (y axis / |y|^2, 1); the stage
    /// takes the frame's wind itself.
    /// </summary>
    private static (Vector4 X, Vector4 Y) CaptureTreeLanes(Kind kind)
    {
        if (!FirstPlacement(kind, out Transform3D placement))
        {
            return (Vector4.Zero, Vector4.Zero);
        }
        // The placement's guest world matrix: Godot model (x, y, z) is the file's (x, -z, y),
        // Godot world the guest's (-x, z, y).
        static Vector3 Guest(Vector3 g) => new(-g.X, g.Z, g.Y);
        Vector3 axisX = Guest(placement.Basis * new Vector3(1f, 0f, 0f));
        Vector3 axisY = Guest(placement.Basis * new Vector3(0f, 0f, -1f));
        Vector3 sx = axisX / MathF.Max(axisX.LengthSquared(), 1e-12f);
        Vector3 sy = axisY / MathF.Max(axisY.LengthSquared(), 1e-12f);
        return (new Vector4(sx.X, sx.Y, sx.Z, TreeSeed(Guest(placement.Origin))), new Vector4(sy.X, sy.Y, sy.Z, 1f));
    }

    /// <summary>The first standing placement of the kind (a batch's first stated copy).</summary>
    private static bool FirstPlacement(Kind kind, out Transform3D placement)
    {
        foreach (GeometryInstance3D source in kind.Sources)
        {
            if (!GodotObject.IsInstanceValid(source) || !source.IsInsideTree())
            {
                continue;
            }
            if (source is MultiMeshInstance3D batch && batch.Multimesh is { } multi)
            {
                if (Refii.Level.Engine_PropShadow.StatedTransforms(multi) is { Length: > 0 } stated)
                {
                    placement = source.GlobalTransform * stated[0];
                    return true;
                }
                continue;
            }
            placement = source.GlobalTransform;
            return true;
        }
        placement = Transform3D.Identity;
        return false;
    }

    /// <summary>xenos_common's `xr_tree_seed` (`sub_82A708E0`): the tree's phase from its position.</summary>
    private static float TreeSeed(Vector3 position)
    {
        static uint Ror13(uint v) => (v >> 13) | (v << 19);
        uint h = unchecked(Ror13(BitConverter.SingleToUInt32Bits(position.X) * 0x24A1u + 0x24DFu) +
            BitConverter.SingleToUInt32Bits(position.Y));
        uint seed = unchecked(Ror13(h * 0x24A1u + 0x24DFu) + BitConverter.SingleToUInt32Bits(position.Z));
        seed = unchecked(Ror13(seed * 9377u + 9439u));
        return (seed % 655360u) / 65536f;
    }

    /// <summary>
    /// A tree material for the capture: the same stages, the shadow mask white, and b4
    /// g_NeedsScattering off (`sub_822109B0`: the capture flag ctx+1796 clears it).
    /// </summary>
    private ShaderMaterial? CaptureMaterial(ShaderMaterial own)
    {
        if (!own.HasMeta(Engine_ClassicMaterial.ClassicMeta))
        {
            return null;
        }
        ulong id = own.GetInstanceId();
        if (_captureMaterials.TryGetValue(id, out ShaderMaterial? held) && GodotObject.IsInstanceValid(held))
        {
            return held;
        }
        var copy = Refii.Models.Engine_MatCopy.Copy(own, "billboard");
        copy.SetShaderParameter(ScreenShadow, Engine_ClassicScreenShadow.White);
        if (copy.GetShaderParameter(XrBooleansName).VariantType != Variant.Type.Nil)
        {
            uint booleans = copy.GetShaderParameter(XrBooleansName).AsUInt32();
            copy.SetShaderParameter(XrBooleansName, booleans & ~(1u << (16 + 4)));
        }
        _captureMaterials[id] = copy;
        return copy;
    }

    /// <summary>
    /// The cards' constants every kind shares, once a frame: the frame's registers, the view and
    /// projection rows, c114 and c31, and the camera's two uniforms on the shared material.
    /// </summary>
    private void FrameRows(Camera3D camera)
    {
        // The rows are the view's: on split screen the first view's from the camera that draws it
        // and the second view's into its own copy of each kind's row (KindRow).
        CardRows(0, Engine_ClassicViews.Camera(0) ?? camera, FramePs, FrameVs);
        if (Engine_ClassicViews.Count > 1 && Engine_ClassicViews.Camera(1) is { } second)
        {
            CardRows(1, second, SecondPs, SecondVs);
        }
        // c47 the eye (view+0x50); c46 the view's z column with its z dropped, normalised: the
        // camera's horizontal forward (0x821C4DB4..0x821C4E7C).
        Vector3 eye = ToGuest(camera.GlobalPosition);
        Vector3 forward = ToGuestDirection(-camera.GlobalBasis.Z);
        var flat = new Vector2(forward.X, forward.Y);
        flat = flat.LengthSquared() > 1e-8f ? flat.Normalized() : Vector2.Zero;
        _cards?.SetShaderParameter(VsCameraPosName, new Vector4(eye.X, eye.Y, eye.Z, 1f));
        _cards?.SetShaderParameter(VsCameraZVectorName, new Vector4(flat.X, flat.Y, 0f, 0f));
    }

    /// <summary>One view's card rows: its frame, c114, its view and projection rows, c31.</summary>
    private void CardRows(int view, Camera3D camera, float[] ps, float[] vs)
    {
        Engine_ClassicConstants.CopyFrameView(view, true, ps);
        Engine_ClassicConstants.CopyFrameView(view, false, vs);
        // VS c114 g_ControlPointWeightingData: (1.5, 2), `sub_82204500(0x131, ...)`.
        Set(vs, 114, 1.5f, 2f, 0f, 0f);
        ViewRows(camera, vs);
        // PS c31.x: 0.25 B.
        Set(ps, 31, 0.25f * Brightness, 0f, 0f, 0f);
    }

    /// <summary>The kind's own card constants onto each view's frame (see VS 0092 / PS 0093), into its row.</summary>
    private void KindRow(Kind kind, int rows)
    {
        if (kind.Row == 0)
        {
            return;
        }
        KindRow(kind, rows, 0, FramePs, FrameVs);
        if (Engine_ClassicViews.Count > 1)
        {
            KindRow(kind, rows, 1, SecondPs, SecondVs);
        }
    }

    private void KindRow(Kind kind, int rows, int view, float[] ps, float[] vs)
    {
        float r = MathF.Max(kind.Sphere.W, 1e-3f);

        // VS c8 g_NormalizedSphereCentre: the model sphere's centre over its radius (the stage lifts
        // a card by radius x c8.z).
        SetVec(vs, 8, new Vector3(kind.Sphere.X / r, kind.Sphere.Y / r, kind.Sphere.Z / r), 1f);
        // VS c113 g_TreeBillboardUVData: uv = corner x (x, w) + (y, z), corner (0, 0) the card's
        // top left. The title's is the impostor's projected extent within its own 256^2 texture, the
        // whole of it give or take half a texel; here the kind's cell of the atlas.
        float u0 = (kind.CellIndex % Columns) / (float)Columns, v0 = (kind.CellIndex / Columns) / (float)rows;
        Set(vs, 113, 1f / Columns, u0, v0, 1f / rows);
        // PS c29: the fade in over [0.75, 0.875] F.
        Set(ps, 29, 0.75f * kind.F, 1f / MathF.Max(0.125f * kind.F, 1e-3f), 0f, 0f);
        Engine_ClassicConstants.SetRowBlockView(view, true, kind.Row, 0, ps);
        Engine_ClassicConstants.SetRowBlockView(view, false, kind.Row, 0, vs);
    }

    /// <summary>
    /// VS c115..c117 g_ViewMatrix and c72..c75 g_ProjMatrix as the stage reads them: view.i =
    /// dot(c(115 + i), p) in the title's D3D view (x right, y up, z ahead, `sub_821B4B48` builds a
    /// left-handed projection), and clip.i = dot(c(72 + i), view). The projection given is the one
    /// that takes that view to Godot's clip space, so the stage's own view-space sizing runs as
    /// the title's and its output is what Godot rasterises (a CPU run of 0092 with these rows
    /// places a card on its sphere, top-left uv (0, 0)).
    /// </summary>
    private static void ViewRows(Camera3D camera, float[] vs)
    {
        Projection view = ViewConvention * new Projection(camera.GlobalTransform.AffineInverse()) * Engine_ClassicShaders.GuestToGodot;
        Projection clip = Engine_ClassicShaders.GuestToClip(camera) * view.Inverse();
        for (int i = 0; i < 3; i++)
        {
            Set(vs, 115 + i, view.X[i], view.Y[i], view.Z[i], view.W[i]);
        }
        for (int i = 0; i < 4; i++)
        {
            Set(vs, 72 + i, clip.X[i], clip.Y[i], clip.Z[i], clip.W[i]);
        }
    }

    /// <summary>Godot's view space (x right, y up, -z ahead) to the title's (z ahead).</summary>
    private static readonly Projection ViewConvention = new(
        new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, -1f, 0f), new Vector4(0f, 0f, 0f, 1f));

    private static void Set(float[] into, int register, float x, float y, float z, float w)
    {
        int at = register * 4;
        into[at] = x;
        into[at + 1] = y;
        into[at + 2] = z;
        into[at + 3] = w;
    }

    private static void SetVec(float[] into, int register, Vector3 v, float w) => Set(into, register, v.X, v.Y, v.Z, w);

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    private static Vector3 ToGuestDirection(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName VsS16Name = "vs_s16";
    private static readonly StringName VsS17Name = "vs_s17";
    private static readonly StringName XrUseGuestPositionName = "xr_use_guest_position";
    private static readonly StringName XrSpecConstantsName = "xr_spec_constants";
    private static readonly StringName XrAlphaThresholdName = "xr_alpha_threshold";
    private static readonly StringName PsDiffuseTextureSamplerName = "ps_g_DiffuseTextureSampler";
    private static readonly StringName PsDiffuseTextureSamplerSecondName = Engine_ClassicShaders.SecondViewSampler("ps_g_DiffuseTextureSampler");
    private static readonly StringName XrBooleansName = "xr_booleans";
    private static readonly StringName VsCameraPosName = "vs_g_CameraPos";
    private static readonly StringName VsCameraZVectorName = "vs_g_CameraZVector";
}
