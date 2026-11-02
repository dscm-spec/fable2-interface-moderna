using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;
using Refii.Models;
using Refii.Host;

namespace Refii.Level;

[Tool]
public partial class Engine_SkyEnvironmentNode : WorldEnvironment
{
    // Shader parameter names as StringNames: a string literal converts to a fresh
    // finalizable StringName on every call.
    private static readonly NodePath Path_Clouds = "Clouds";
    private static readonly NodePath Path_SunDisc = "SunDisc";
    private static readonly NodePath Path_SunGlare = "SunGlare";
    private static readonly NodePath Path_MoonGlare = "MoonGlare";
    private static readonly NodePath Path_Water = Engine_LevelScene.WaterNodeName;
    private static readonly NodePath Path_Celestial = Engine_CelestialElements.NodeName;
    private static readonly NodePath Path_Precipitation = Engine_DynamicPrecipitation.NodeName;
    private static readonly NodePath Path_Weather = "Weather";
    private static readonly NodePath Path_Sun = Engine_LevelLighting.SunNodeName;
    private static readonly StringName Param_cloud_density = "cloud_density";
    private static readonly StringName Param_element_vis = "element_vis";
    private static readonly StringName Param_sun_engine = "sun_engine";
    private static readonly StringName Param_uv_scale_offset = "uv_scale_offset";
    private static readonly StringName Param_moon_phase = "moon_phase";
    private static readonly StringName Param_moon_dir_world = "moon_dir_world";
    private static readonly StringName Global_atmos_sun = "atmos_sun";
    private static readonly StringName Global_dynamic_sun_toward = "dynamic_sun_toward";
    private static readonly StringName Global_dynamic_sun_radiance = "dynamic_sun_radiance";

    private float _dayFraction = 0.5f;
    private float _guestDayFraction = 0.5f;
    private int _guestDay;
    private static float _thunderSfx = 1f;

    [Export(PropertyHint.Range, "0,24,0.05,suffix:h")]
    public float TimeOfDay
    {
        get => _dayFraction * 24f;
        set
        {
            _dayFraction = value / 24f;
            _guestDayFraction = _dayFraction;
            if (IsNodeReady())
            {
                Reevaluate();
            }
        }
    }

    [Export]
    public string LevelGdbPath { get; set; } = string.Empty;

    public SkyTheme? Theme { get; set; }

    public List<byte[]> LightingGdbs { get; } = new();

    public DirectionalLight3D? Sun { get; set; }

    /// <summary>
    /// The region's lighting for the current time of day, or null before the first evaluation.
    ///
    /// Everything in the region that has to be lit reads it from here — the prop streamer asks
    /// for it as each mesh arrives. One node owns the answer, so the editor and the game cannot
    /// end up with two.
    /// </summary>
    public LightingFrame? Lighting { get; private set; }

    /// <summary>The region's painted day sets, sampled at the hero; null when it paints none.</summary>
    public Refii.Lighting.Engine_PaintedDaySets? Painted { get; set; }

    /// <summary>
    /// Each painted day set's lighting at both ends of the current step, by hash; null when no
    /// loaded gdb states it. Folded every frame at the layer's live weight (<see cref="LightingNow"/>).
    /// </summary>
    private readonly Dictionary<uint, (LightingTheme Start, LightingTheme? End)?> _paintedThemes = new();

    public ToneMapTheme? ToneMap => _toneMapTheme;

    public SkyFrame? Frame => _frame;

    /// <summary>
    /// The environment node for the world currently standing, or null before one is loaded.
    ///
    /// Godot allows one environment per World3D and Refii loads one region at a time, so this
    /// mirrors a real singleton rather than inventing one. It exists because the things that
    /// need the lighting are not all INSIDE the region: <c>GameEntities</c> hangs beside the
    /// region under <c>GameWorld</c>, carrying the guest's live characters, and a sibling
    /// cannot reach the region's environment by walking its own parents.
    ///
    /// If a second region is ever mounted at the same time, this becomes the wrong answer for
    /// one of them and the lookup has to become per-World3D.
    /// </summary>
    public static Engine_SkyEnvironmentNode? Current { get; private set; }

    /// <summary>
    /// Evaluate as the region stands up, in the EDITOR as well as in the game.
    ///
    /// The scene is saved with the sun and the environment already correct, but nothing
    /// standing in it carries any lighting: props are packed meshless and their instance
    /// parameters cannot be serialised. So the first evaluation is not a refresh, it is the
    /// only time the region is lit at all.
    ///
    /// It RETRIES, because the editor can lose the race. The theme is read back out of the
    /// banks through <c>Engine_BnkMeshLoader</c>, the addon mounts them asynchronously, and a
    /// level scene reopened at editor start arrives first. One attempt then finds no GDB, gives
    /// up, and the region stays on its defaults for the rest of the session with nothing said —
    /// which is exactly the kind of silent editor-only difference this is meant to remove.
    /// </summary>
    public override void _Ready()
    {
        Current = this;
        Engine_DynamicSkyLighting.Reset();
        Refii.Fx.Engine_FxSystem.BindAmbientToScene();

        try
        {
            System.Collections.Generic.IReadOnlyDictionary<string, string> config =
                Refii.Core.Engine_RefiiConfig.Read(out _);
            RenderingServer.GlobalShaderParameterSet("fable_light_debug",
                Refii.Core.Engine_RefiiConfig.Int(config, "lighting_debug_view", 0));

            // ONE bake weight for every authored bake — terrain lightmap, vertex colours,
            // instance colours, interior textures — so the bridge, the rocks and the ground
            // they meet scale together instead of drifting into separate lighting worlds.
            float bakeWeight = System.Math.Clamp(
                Refii.Core.Engine_RefiiConfig.Float(config, "baked_light_weight", 0.45f),
                0.05f, 2.0f);
            Refii.Lighting.Engine_LevelBakedLighting.BakeWeight = bakeWeight;
            Refii.Lighting.Engine_LevelBakedLighting.DialProbeLitProps =
                Refii.Core.Engine_RefiiConfig.Bool(config, "prop_probe_bake_weight", false);
            RenderingServer.GlobalShaderParameterSet("fable_bake_weight", bakeWeight);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[lighting] lighting cvars unreadable: {e.Message}");
        }

        Engine_PostFxEffect.RegisterGlobals();
        if (Engine_PostFxParams.SunSourcesRequested)
        {
            Engine_PostFxSunSources.Ensure(this);
        }

        Reevaluate();
    }

    public override void _ExitTree()
    {
        Engine_WeatherShelter.Reset();
        if (Current == this)
        {
            Current = null;
            Engine_DynamicSkyLighting.Reset();
            Engine_DynamicSurfaceWeather.Reset();
            RenderingServer.GlobalShaderParameterSet("dynamic_cloud_projection", Vector4.Zero);
        }

        DetachEditorCompositor();
    }

    /// <summary>
    /// TWO JOBS SHARE THE FRAME, and they have opposite lifetimes.
    ///
    /// The mount RETRY above runs until the theme arrives and then never again. The sky's
    /// ANIMATION — cloud layers drifting on their authored velocities, and the sun and moon
    /// glare occluding against them — runs for as long as the region stands. Processing used to
    /// be switched off the moment the retry was satisfied, so wiring the animation into the same
    /// callback without this split would stop the clouds on the frame the region finished
    /// lighting itself.
    /// </summary>
    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("skyenvironmentnode");
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Current == this)
        {
            Refii.Lighting.Engine_GiRefresh.Tick(Environment, GetViewport()?.GetCamera3D(), delta);
        }

        Refii.Core.Engine_FramePhases.Lap("sky.gi", ref lap);

        Engine_WaterMaterial.AdvancePhases(GetParent()?.GetNodeOrNull<Node3D>(Path_Water), (float)delta);
        if (Current == this)
        {
            Refii.Models.Engine_SceneWind.Pump();
        }

        if (Lighting is null && _attempts <= MountAttempts)
        {
            if (++_attempts > MountAttempts)
            {
                GD.Print($"[lighting] {Name}: no environment theme after {MountAttempts} " +
                    "frames; the region is on shader defaults");
            }
            else
            {
                Reevaluate();
            }
        }

        FollowGuestClock();
        Refii.Core.Engine_FramePhases.Lap("sky.clock", ref lap);
        Engine_LevelBakedLighting.PumpRefresh(RelightBudgetMs);
        Refii.Core.Engine_FramePhases.Lap("sky.relight", ref lap);
        TickPainted((float)delta);
        Refii.Core.Engine_FramePhases.Lap("sky.painted", ref lap);
        TrackBodies((float)delta);
        Refii.Core.Engine_FramePhases.Lap("sky.bodies", ref lap);
        AnimateSky((float)delta);
        Engine_WeatherShelter.Tick((float)delta);
        FollowSkyChoice();
        Engine_DynamicWeatherChoice.Tick((float)delta);
        if (Engine_DynamicSky.Enabled &&
            Environment?.Sky?.SkyMaterial is ShaderMaterial dynamicMaterial)
        {
            _dynamicSky ??= new Engine_DynamicSkyDriver();
            if (_frame is { } dynamicFrame)
            {
                _dynamicSky.SetDaylight(Engine_DynamicSky.Daylight(dynamicFrame));
            }

            _dynamicSky.SetShelter(Engine_WeatherShelter.Exposure, Engine_WeatherShelter.Falling);
            _dynamicSky.SetNightClock(_dayFraction * 24f, _guestDay,
                Engine_DynamicNightSky.ParseMode(Refii.Core.Engine_Switches.String(Engine_DynamicNightSky.AuroraKey, "auto")));
            if (!Engine.IsEditorHint())
            {
                if (_dynamicThunder is null)
                {
                    _dynamicThunder = new Engine_DynamicThunder { Name = "DynamicThunder" };
                    AddChild(_dynamicThunder);
                }
                _dynamicThunder.Bind(_dynamicSky);
                var volume = HostBridge.GetOptionSliders();
                if (volume.Known) _thunderSfx = volume.Sfx;
                _dynamicThunder.Advance((float)delta, Engine_WeatherShelter.Exposure, _thunderSfx);
            }
            _dynamicSky.Tick((float)delta, dynamicMaterial,
                GetParent()?.GetNodeOrNull<Engine_DynamicPrecipitation>(Path_Precipitation),
                Environment);
            if (Current == this && !Engine.IsEditorHint())
            {
                if (_surfaceInteractions is null && Engine_DynamicSurfaceWeather.Enabled)
                {
                    _surfaceInteractions = new Engine_DynamicSurfaceInteractions { Name = "DynamicSurfaceInteractions", ProcessPriority = 100 };
                    AddChild(_surfaceInteractions);
                }
                if (_surfaceInteractions is { } interactions)
                {
                    interactions.Ground = InteractionGround;
                    interactions.GroundRoot = GetParent() as Node3D;
                    interactions.QueueAdvance(ResolveCamera()?.GlobalPosition ?? Vector3.Zero, _dynamicSky.Weather,
                        _dynamicSky.SurfaceState, Engine_DynamicSurfaceWeather.Enabled,
                        _frame is { } contactFrame ? Engine_DynamicSky.Daylight(contactFrame) : 0f);
                }
            }
        }
        else
        {
            _dynamicThunder?.Bind(null);
            _surfaceInteractions?.Disable();
            if (Current == this) Engine_DynamicSurfaceWeather.Reset();
        }

        if (Current == this)
        {
            Engine_DynamicSkyLighting.Update(_dynamicSky?.Weather ?? default,
                _frame is { } weatherFrame ? Engine_DynamicSky.Daylight(weatherFrame) : 0f,
                Engine_DynamicSky.Enabled && Engine_DynamicSky.LightingOverride);
            if (_frame is { } sunFrame)
                Engine_DynamicSkyLighting.ConfigureSun(sunFrame, _dynamicSky?.Weather ?? default,
                    Engine_DynamicSky.Enabled && Engine_DynamicSky.LightingOverride, Engine_DynamicSky.CloudShadows,
                    Lighting?.HasSky ?? false);
            if (Lighting is { } weatherLighting)
            {
                if (ResolveSun() is { } weatherSun) Engine_LevelLighting.UpdateSun(weatherSun, weatherLighting);
                Engine_LevelBakedLighting.PublishSunLighting(weatherLighting);
            }
            RenderingServer.GlobalShaderParameterSet(Global_dynamic_sun_toward, Engine_DynamicSkyLighting.SunToward);
            RenderingServer.GlobalShaderParameterSet(Global_dynamic_sun_radiance, Engine_DynamicSkyLighting.OverridesSun
                ? Engine_DynamicSkyLighting.SunColour * Engine_DynamicSkyLighting.SunEnergy : Vector3.Zero);
        }

        Engine_DynamicCloudShadows.Sync(this, ref _dynamicCloudShadowCaster,
            Engine_DynamicSky.Enabled ? Environment?.Sky?.SkyMaterial as ShaderMaterial : null,
            Engine_DynamicSky.LightingOverride && Engine_DynamicSky.CloudShadows,
            ResolveCamera(), ResolveSun());

        Refii.Core.Engine_FramePhases.Lap("sky.anim", ref lap);
        if (Engine_PostFxParams.SunSourcesRequested)
        {
            Engine_PostFxSunSources.Ensure(this);
        }

        PublishPostFx();
        SyncEditorCompositor();
        Refii.Core.Engine_FramePhases.Lap("sky.postfx", ref lap);
    }

    /// <summary>
    /// Frozen means the guest is not advancing time, so neither do we.
    /// </summary>
    private float _lastEvaluated = float.NegativeInfinity;

    private readonly HashSet<uint> _paintedUnresolved = new();

    /// <summary>Main-thread time a frame may spend on a queued re-light (Engine_LevelBakedLighting.PumpRefresh).</summary>
    private const double RelightBudgetMs = 1.5;

    /// <summary>Report a re-evaluation that costs more than a frame at 144 Hz.</summary>
    private const double ReevaluateWarnMs = 6.9;

    /// <summary>
    /// How far the day moves between theme extracts: five game minutes.
    /// </summary>
    /// <remarks>
    /// The extract walks the GDB chain for the sky, cloud, water, tone and lighting themes at both
    /// ends of the step. Nothing on screen holds for the step: the sun and the lighting are
    /// interpolated between the two ends every frame (<see cref="TrackBodies"/>), the atmosphere
    /// at the guest's 15 Hz (<see cref="TickAtmosphere"/>). The blend between day-set entries is
    /// linear in time, so the interpolation is exact except where an entry boundary falls inside
    /// a step.
    /// </remarks>
    private const float ReevaluateStep = 5.0f / 1440.0f;

    /// <summary>
    /// Drives the region's time of day from the guest's own world clock.
    /// </summary>
    /// <remarks>
    /// UNTIL THIS EXISTED THE WHOLE ENVIRONMENT WAS FROZEN. `Reevaluate` ran only while
    /// <c>Lighting</c> was null, so the first success was the last: the sun, the sky, the ambient,
    /// the fog and the cloud tint were all pinned to whichever day-set entry happened to be
    /// selected at level load, and the day advanced underneath with no effect whatsoever.
    ///
    /// A HOST-SIDE CLOCK WOULD NOT DO. The value has to be the guest's, because the guest is the
    /// thing that decides what time it is — it fast-forwards for sleep, it stops for cutscenes,
    /// and script sets it outright. Ticking our own would drift within one night's sleep.
    /// </remarks>
    private LightingTheme? _lightingTheme;

    /// <summary>
    /// THE THEMES AT BOTH ENDS OF THE CURRENT STEP; see <see cref="ReevaluateStep"/>.
    /// </summary>
    private SkyTheme? _skyStepStart;
    private SkyTheme? _skyStepEnd;
    private LightingTheme? _lightingStepEnd;
    private WaterTheme? _waterStepStart;
    private WaterTheme? _waterStepEnd;
    private CloudTheme? _cloudStepStart;
    private CloudTheme? _cloudStepEnd;
    private ToneMapTheme? _toneMapStepStart;
    private ToneMapTheme? _toneMapStepEnd;
    private float _stepStart = float.NaN;

    /// <summary>
    /// How often the atmosphere follows the clock between steps: the guest's own tick rate
    /// (`sub_8218EAF8` runs every 15 Hz tick). The dome, fog, water, clouds and tone curve used
    /// to hold for the whole five-minute step and then jump.
    /// </summary>
    private const float AtmosphereHz = 15f;

    private float _atmosphereClock;
    private ImageTexture? _domeLut;

    /// <summary>The per-instance bake dial the region was last lit with; NaN until it has been.</summary>
    private float _instanceDial = float.NaN;

    private static float WrapForward(float from, float to)
    {
        float d = to - from;
        if (d < -0.5f)
        {
            d += 1f;
        }

        return d;
    }

    private float StepWeight() =>
        float.IsNaN(_stepStart)
            ? 0f
            : Mathf.Clamp(WrapForward(_stepStart, _dayFraction) / ReevaluateStep, 0f, 1f);

    /// <summary>
    /// Moves the sun, the moon and the light EVERY FRAME.
    ///
    /// <see cref="Reevaluate"/> only runs on a five-game-minute step because the theme extract and
    /// the dome's scatter table are expensive — but that cadence is also what the bodies moved on,
    /// so they stepped instead of travelling. Everything here is arithmetic on the theme already
    /// in hand: the orbit is trigonometry and the light is a basis, so it costs nothing to do per
    /// frame. The scatter LUT is deliberately NOT rebuilt here; it changes slowly and the coarse
    /// pass still owns it.
    /// </summary>
    private void TrackBodies(float delta)
    {
        if (Theme is null || !float.IsFinite(_dayFraction))
        {
            return;
        }

        // Between the step's two themes, by the clock — see _skyStepEnd.
        float w = StepWeight();
        if (_skyStepStart is { } skyStart && _skyStepEnd is { } skyEnd && w > 0f)
        {
            Theme = Engine_ThemeTimeline.BlendSky(skyStart, skyEnd, w);
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);
        _frame = frame;
        TickAtmosphere(delta, w, frame);

        Vector3 dome = Engine_SkyFrame.ToDome(frame.SunDirection);
        if (Environment?.Sky?.SkyMaterial is ShaderMaterial material)
        {
            material.SetShaderParameter(Param_sun_engine, dome);
        }

        RenderingServer.GlobalShaderParameterSet(Global_atmos_sun, dome);

        if (Engine_DynamicSky.Enabled &&
            Environment?.Sky?.SkyMaterial is ShaderMaterial dynamicMaterial)
        {
            Vector3 moon = Engine_SkyFrame.ToGodot(frame.MoonDirection);
            dynamicMaterial.SetShaderParameter(Param_moon_phase, MoonPhaseNow / 8f);
            if (moon != _lastMoon)
            {
                _lastMoon = moon;
                dynamicMaterial.SetShaderParameter(Param_moon_dir_world, moon);
            }
        }

        if (GetParent()?.GetNodeOrNull<Node3D>(Path_Celestial) is { } celestial)
        {
            Engine_CelestialElements.Update(celestial, Theme, frame, null, MoonPhaseNow);
        }

        if (LightingNow(w) is { } lightingNow && ResolveSun() is { } sun)
        {
            LightingFrame lighting = Engine_LightingFrame.Evaluate(Theme, lightingNow, frame);
            Lighting = lighting;
            if (Current != this) Engine_LevelLighting.UpdateSun(sun, lighting);
            if (Environment is not null)
            {
                Engine_LevelLighting.ConfigureEnvironment(Environment, lighting);
            }

            RelightIfDialMoved(lighting);
        }
    }

    /// <summary>
    /// The level day set's lighting at weight <paramref name="w"/> through the step, with every
    /// painted layer folded over it at its live weight — the guest's fold (`sub_8218EAF8`).
    /// </summary>
    private LightingTheme? LightingNow(float w)
    {
        if (_lightingTheme is not { } start)
        {
            return null;
        }

        LightingTheme now = _lightingStepEnd is { } end && w > 0f
            ? Engine_ThemeTimeline.BlendLighting(start, end, w)
            : start;

        if (Painted is { Any: true } painted)
        {
            foreach (Refii.Lighting.Engine_PaintedDaySets.Layer layer in painted.Layers)
            {
                if (layer.Weight > 0f &&
                    _paintedThemes.TryGetValue(layer.Hash, out var stated) && stated is { } pair)
                {
                    LightingTheme layerNow = pair.End is { } layerEnd && w > 0f
                        ? Engine_ThemeTimeline.BlendLighting(pair.Start, layerEnd, w)
                        : pair.Start;
                    now = Engine_LightingTheme.FoldPainted(now, layerNow, layer.Weight);
                }
            }
        }

        return now;
    }

    /// <summary>
    /// Re-lights the region when the one per-instance value that is not the theme's — the
    /// prop bake dial — has changed, and once when the region first stands: packed props
    /// carry no instance parameters until something writes them.
    /// </summary>
    private void RelightIfDialMoved(LightingFrame lighting)
    {
        float dial = Engine_LevelBakedLighting.DialProbeLitProps
            ? Engine_LevelBakedLighting.AuthoredBakeWeight
            : 1f;
        if (dial == _instanceDial || GetParent() is not Node region)
        {
            return;
        }

        _instanceDial = dial;
        Engine_LevelBakedLighting.QueueRefreshInstanceLighting(region.GetParent() ?? region, lighting);
    }

    /// <summary>
    /// The dome, fog, water, clouds and tone curve, between the step's two themes, at
    /// <see cref="AtmosphereHz"/>.
    /// </summary>
    private void TickAtmosphere(float delta, float w, SkyFrame frame)
    {
        _atmosphereClock += delta;
        if (_atmosphereClock < 1f / AtmosphereHz || Theme is null)
        {
            return;
        }

        _atmosphereClock = 0f;
        ApplyAtmosphere(frame, w, report: false);
    }

    private void ApplyAtmosphere(SkyFrame frame, float w, bool report)
    {
        if (Theme is null)
        {
            return;
        }

        (AtmosphereState domeState, ImageTexture domeLut) =
            Engine_SkyEnvironment.ComputeDome(frame, Theme, _domeLut);
        _domeLut = domeLut;

        if (Environment?.Sky?.SkyMaterial is ShaderMaterial material)
        {
            Engine_SkyEnvironment.ApplyDomeUniforms(material, frame, domeState, domeLut);
        }

        // OUTSIDE the sky-material guard. The world is fogged whether or not this region drew a
        // dome, and a level whose sky material failed to build would otherwise lose its fog too —
        // which is the harder of the two failures to spot.
        Engine_SkyEnvironment.ApplyAtmosphereGlobals(frame, Theme, domeState, domeLut, report);

        Node3D? water = GetParent()?.GetNodeOrNull<Node3D>(Path_Water);
        if (water is not null)
        {
            WaterTheme waterTheme = _waterStepStart is { } waterStart
                ? (_waterStepEnd is { } waterEnd && w > 0f
                    ? Engine_ThemeTimeline.BlendWater(waterStart, waterEnd, w)
                    : waterStart)
                : WaterTheme.Default;

            // By index: GetChildren() is a Godot Array, a finalizable handle per tick.
            for (int i = 0, count = water.GetChildCount(); i < count; i++)
            {
                if (water.GetChild(i) is MeshInstance3D waterMesh &&
                    waterMesh.MaterialOverride is ShaderMaterial waterMaterial)
                {
                    Engine_SkyEnvironment.ApplyWaterFrame(
                        waterMaterial, frame, domeState, domeLut);
                    Engine_WaterMaterial.Apply(waterMaterial, waterTheme);
                }
            }

            Engine_LevelScene.ApplyVistaOceanGate(water, waterTheme);
        }

        if (_cloudStepStart is { } cloudStart)
        {
            CloudTheme cloudTheme = _cloudStepEnd is { } cloudEnd && w > 0f
                ? Engine_ThemeTimeline.BlendClouds(cloudStart, cloudEnd, w)
                : cloudStart;
            if (GetParent()?.GetNodeOrNull<Node3D>(Path_Clouds) is { } clouds)
            {
                Engine_Clouds.Update(clouds, cloudTheme, frame.MainLightDirection, frame.MainLightColour);
            }

            _cloudTheme = cloudTheme;
        }

        if (_toneMapStepStart is { } toneStart)
        {
            _toneMapTheme = _toneMapStepEnd is { } toneEnd && w > 0f
                ? Engine_ThemeTimeline.BlendToneMap(toneStart, toneEnd, w)
                : toneStart;
        }
    }

    /// <summary>
    /// The guest's painted-day-set blend, at the hero, on real time. The weights are folded every
    /// frame (<see cref="LightingNow"/>); a re-evaluate is needed only to read a day set the hero
    /// has just walked into.
    /// </summary>
    private void TickPainted(float delta)
    {
        if (Painted is null || Lighting is null)
        {
            return;
        }

        // IN THE REGION'S OWN FRAME. The level root is scaled (-1, 1, 1) — the guest's x runs
        // the other way along Godot's X — and the grid was laid out in the frame the terrain
        // was built in, so a world-space hero at X = -135 reads outside a grid that starts at
        // 0 (b176). The parent's inverse takes the hero back into that frame.
        Vector3? hero = Refii.Game.GameEntities.HeroPosition;
        if (hero is { } world && GetParent() is Node3D root)
        {
            hero = root.GlobalTransform.AffineInverse() * world;
        }

        Painted.Tick(hero, delta);
        foreach (Refii.Lighting.Engine_PaintedDaySets.Layer layer in Painted.Layers)
        {
            if (layer.Weight > 0f && !_paintedThemes.ContainsKey(layer.Hash))
            {
                Reevaluate(restartStep: false);
                return;
            }
        }
    }

    private void FollowGuestClock()
    {
        var clock = HostBridge.TimeOfDay();
        if (clock is { } current)
        {
            _guestDayFraction = current.Fraction;
            _guestDay = current.DayCount;
        }
        _dayFraction = Engine_DynamicNightSky.ResolveTime(_guestDayFraction, Engine_DynamicSky.Enabled,
            Refii.Core.Engine_Switches.String(Engine_DynamicNightSky.TimeKey, "auto"));

        // Wrapped distance, so midnight is not a jump. Crossing 0.999 -> 0.001 is two minutes of
        // game time, and comparing the raw difference would read it as most of a day and force a
        // rebuild every midnight.
        float moved = MathF.Abs(_dayFraction - _lastEvaluated);
        moved = MathF.Min(moved, 1.0f - moved);

        if (float.IsFinite(_lastEvaluated) && moved < ReevaluateStep)
        {
            return;
        }

        _lastEvaluated = _dayFraction;
        GD.Print($"[sky-clock] tod={_dayFraction * 24f:0.00}h (fraction {_dayFraction:0.000}) " +
            $"day {_guestDay}{(clock?.Frozen == true ? ", FROZEN" : string.Empty)}");

        // WHERE THE SUN IS, in world space — the one direction every consumer takes.
        //
        // The celestial billboards read it through `VIEW_MATRIX` (world, no MODEL_MATRIX, so the
        // region root's mirror does not apply); the post-FX projects the same vector through the
        // camera; the light is the only node-parented consumer and pre-compensates the mirror
        // itself. Measuring the disc mesh's GlobalPosition here was wrong — it is placed in the
        // shader and its node never moves.
        if (Theme is { } probeTheme)
        {
            SkyFrame probe = Engine_SkyFrame.Evaluate(probeTheme, _dayFraction);
            Vector3 world = Engine_SkyFrame.ToGodot(probe.SunDirection).Normalized();
            GD.Print($"[sky-sun] world ({world.X:0.00},{world.Y:0.00},{world.Z:0.00}) " +
                $"elevation {Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(world.Y, -1f, 1f))):0.0}deg");
        }

        // TIMED, because "it stutters about once a second" and "Reevaluate is expensive" are two
        // different claims and only one of them is measured. If this is not the cost, the number
        // says so and the search moves elsewhere instead of the interval being tuned on a hunch.
        ulong began = Time.GetTicksUsec();
        Reevaluate();
        double ms = (Time.GetTicksUsec() - began) / 1000.0;

        if (ms > ReevaluateWarnMs)
        {
            // BROKEN DOWN BY PHASE. A single total cannot distinguish the theme extract from the
            // dome LUT upload from the instance walk, and those want completely different fixes.
            GD.Print(
                $"[lighting] re-evaluate took {ms:0.0} ms at tod={_dayFraction:0.0000} " +
                $"(theme {_msTheme:0.0}, atmosphere {_msDome:0.0}, " +
                $"celestial {_msCelestial:0.0}, instances {_msInstances:0.0})");
        }
    }

    private bool _reportedSunAgreement;

    private double _msTheme;
    private double _msDome;
    private double _msCelestial;
    private double _msInstances;

    private static double SinceMs(ref ulong mark)
    {
        ulong now = Time.GetTicksUsec();
        double ms = (now - mark) / 1000.0;
        mark = now;
        return ms;
    }

    /// <summary>Frames to keep asking for the theme. Generous — a cold bank mount is slow.</summary>
    private const int MountAttempts = 600;

    private int _attempts;

    public void Initialise(SkyTheme theme, float dayFraction, string levelGdbPath)
    {
        Theme = theme;
        _dayFraction = dayFraction;
        _guestDayFraction = dayFraction;
        LevelGdbPath = levelGdbPath;
    }

    /// <param name="restartStep">
    /// Whether this begins a NEW five-minute blend window. True for the clock, which is what the
    /// window measures. FALSE for a painted-weight change, which is a POSITIONAL event: the step
    /// is not over, only its content changed.
    ///
    /// Resetting the phase on a painted trigger would snap the drawn lighting back to the step's
    /// start; holding it keeps the lighting continuous.
    /// </param>
    public void Reevaluate(bool restartStep = true)
    {
        if (!EnsureTheme() || Theme is null)
        {
            return;
        }

        ulong mark = Time.GetTicksUsec();

        _skyStepStart = null;
        _skyStepEnd = null;
        _lightingStepEnd = null;
        _waterStepStart = null;
        _waterStepEnd = null;
        _cloudStepStart = null;
        _cloudStepEnd = null;
        _toneMapStepStart = null;
        _toneMapStepEnd = null;
        if (restartStep || float.IsNaN(_stepStart))
        {
            _stepStart = _dayFraction;
        }

        // BOTH ENDS AT THE STEP'S OWN TIMES, so the per-frame interpolation is exact however far
        // into the step a painted re-evaluate lands.
        float stepStartTime = _stepStart;
        float stepEndTime = (_stepStart + ReevaluateStep) % 1f;

        if (LightingGdbs.Count != 0 &&
            Engine_ThemeTimeline.TryExtractSkyBlended(
                LightingGdbs, stepStartTime, out SkyTheme startTheme))
        {
            Theme = startTheme;
            _skyStepStart = startTheme;
            if (Engine_ThemeTimeline.TryExtractSkyBlended(
                    LightingGdbs, stepEndTime, out SkyTheme endTheme))
            {
                _skyStepEnd = endTheme;
            }
        }

        float w = StepWeight();
        if (_skyStepStart is { } skyStart && _skyStepEnd is { } skyEnd && w > 0f)
        {
            Theme = Engine_ThemeTimeline.BlendSky(skyStart, skyEnd, w);
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);

        if (LightingGdbs.Count != 0)
        {
            if (Engine_ThemeTimeline.TryExtractWaterBlended(
                    LightingGdbs, stepStartTime, out WaterTheme waterStart))
            {
                _waterStepStart = waterStart;
                if (Engine_ThemeTimeline.TryExtractWaterBlended(
                        LightingGdbs, stepEndTime, out WaterTheme waterEnd))
                {
                    _waterStepEnd = waterEnd;
                }
            }

            if (Engine_ThemeTimeline.TryExtractCloudsBlended(
                    LightingGdbs, stepStartTime, out CloudTheme cloudStart))
            {
                _cloudStepStart = cloudStart;
                if (Engine_ThemeTimeline.TryExtractCloudsBlended(
                        LightingGdbs, stepEndTime, out CloudTheme cloudEnd))
                {
                    _cloudStepEnd = cloudEnd;
                }
            }

            if (Engine_ThemeTimeline.TryExtractToneMapBlended(
                    LightingGdbs, stepStartTime, out ToneMapTheme toneStart))
            {
                _toneMapStepStart = toneStart;
                if (Engine_ThemeTimeline.TryExtractToneMapBlended(
                        LightingGdbs, stepEndTime, out ToneMapTheme toneEnd))
                {
                    _toneMapStepEnd = toneEnd;
                }
            }
        }

        _msTheme = SinceMs(ref mark);

        ApplyAtmosphere(frame, w, report: true);
        _atmosphereClock = 0f;
        _msDome = SinceMs(ref mark);

        Node3D? celestial = GetParent()?.GetNodeOrNull<Node3D>(Path_Celestial);
        if (celestial is not null)
        {
            Engine_CelestialElements.Update(celestial, Theme, frame, null, MoonPhaseNow);
        }

        _msCelestial = SinceMs(ref mark);
        _frame = frame;

        if (Engine_DynamicSky.Enabled &&
            Environment?.Sky?.SkyMaterial is ShaderMaterial dynamicMaterial)
        {
            Engine_DynamicSky.ApplyFrame(dynamicMaterial, frame, Theme);
            dynamicMaterial.SetShaderParameter(Param_moon_phase, MoonPhaseNow / 8f);
            _dynamicSky ??= new Engine_DynamicSkyDriver();
            if (LightingGdbs.Count != 0 &&
                Engine_EnvironmentTheme.TryExtractWeather(LightingGdbs, out WeatherTheme dynamicWeather))
            {
                _dynamicSky.FollowTheme(dynamicWeather);
            }

            Vector2 wind = Refii.Models.Engine_MdlMaterial.WindDirection;
            Engine_DynamicSky.ApplyWind(dynamicMaterial, new Vector2(-wind.X, wind.Y));
            _dynamicSky.SetWind(new Vector2(-wind.X, wind.Y));
        }

        if (LightingGdbs.Count == 0 ||
            !Engine_ThemeTimeline.TryExtractLightingBlended(
                LightingGdbs, stepStartTime, out LightingTheme lightingTheme))
        {
            return;
        }

        _lightingTheme = lightingTheme;
        if (Engine_ThemeTimeline.TryExtractLightingBlended(
                LightingGdbs, stepEndTime, out LightingTheme lightingEnd))
        {
            _lightingStepEnd = lightingEnd;
        }

        // THE PAINTED LAYERS' OWN DAY SETS, at both ends of the step. Folded over the level day
        // set every frame by weight (LightingNow). A hash the chain cannot resolve is said once
        // and remembered as null, so walking into it does not re-evaluate every frame.
        _paintedThemes.Clear();
        if (Painted is { Any: true } painted)
        {
            foreach (Refii.Lighting.Engine_PaintedDaySets.Layer layer in painted.Layers)
            {
                if (Engine_ThemeTimeline.TryExtractLightingBlended(
                        LightingGdbs, stepStartTime, layer.Hash, out LightingTheme paintedStart))
                {
                    _paintedThemes[layer.Hash] = (paintedStart,
                        Engine_ThemeTimeline.TryExtractLightingBlended(
                            LightingGdbs, stepEndTime, layer.Hash, out LightingTheme paintedEnd)
                            ? paintedEnd
                            : null);
                }
                else
                {
                    _paintedThemes[layer.Hash] = null;
                    if (_paintedUnresolved.Add(layer.Hash))
                    {
                        GD.Print($"[painted] day set 0x{layer.Hash:X8} is painted but no loaded gdb states it");
                    }
                }
            }
        }

        LightingFrame lighting = Engine_LightingFrame.Evaluate(Theme, LightingNow(w) ?? lightingTheme, frame);
        Lighting = lighting;

        if (Environment is not null)
        {
            Engine_LevelLighting.ConfigureEnvironment(Environment, lighting);
        }

        DirectionalLight3D? sun = ResolveSun();
        if (sun is not null)
        {
            Engine_LevelLighting.UpdateSun(sun, lighting);

            // DOES THE LIGHT AGREE WITH THE DISC? Reported once, in WORLD space, because that is
            // the only space where the question is answerable.
            //
            // Both the sun disc and this light derive from the same stated SunDirection but reach
            // the screen through different sign conventions — the celestial elements log +0.44 in X
            // where the sky frame states -0.44 — and each may or may not sit under the level root's
            // Scale(-1, 1, 1). On paper the two cancel; the reported symptom is that they do not.
            // Reading the two nodes' actual global transforms settles it without guessing, and a
            // sign flipped on a hunch is a coin toss that would look like a fix either way.
            //
            // EXPECTED: `travel` is the direction light moves, so it should be the NEGATION of
            // `disc` (the direction toward the sun). A dot near -1 is correct; near +1 means the
            // light arrives from precisely the wrong side.
            if (!_reportedSunAgreement && sun.IsInsideTree())
            {
                Vector3 travel = -sun.GlobalTransform.Basis.Column2;
                Node3D? discOwner = GetParent()?.GetNodeOrNull<Node3D>(Path_Celestial);
                Node3D? disc = discOwner?.GetNodeOrNull<Node3D>(Path_SunDisc);

                if (disc is not null)
                {
                    _reportedSunAgreement = true;

                    // AGAINST THE RIG, NOT THE DISC. `UpdateSun` consumes the LIGHTING frame's
                    // direction, and the two are deliberately different quantities:
                    //
                    //   - the light rig turns on LightRigXYRotation alone
                    //   - the visible disc adds SunAxisXYRotationOffset and SunAxisZOffset
                    //
                    // Measured across the shipped themes they disagree on 209 of 348 samples, worst
                    // case 156.5 degrees — so a scene lit from almost opposite the visible sun can
                    // be entirely faithful. Comparing against the disc would mean "fixing" the rig
                    // until it matched, destroying a separation the game has on purpose.
                    Vector3 rig = new(
                        lighting.SunDirection[0],
                        lighting.SunDirection[1],
                        lighting.SunDirection[2]);

                    Vector3 discDir = new(
                        frame.SunDirection[0], frame.SunDirection[1], frame.SunDirection[2]);

                    rig = rig.LengthSquared() > 1e-8f ? rig.Normalized() : Vector3.Up;
                    discDir = discDir.LengthSquared() > 1e-8f ? discDir.Normalized() : Vector3.Up;

                    GD.Print(
                        $"[lighting] sun agreement: rig={rig} travel={travel} " +
                        $"rigDot={travel.Dot(rig):0.000} (expect -1.0; anything else is OUR bug) " +
                        $"| disc={discDir} rigVsDisc={Mathf.RadToDeg(rig.AngleTo(discDir)):0.0}deg " +
                        "(expected to differ; the game lights from the rig, not the disc)");
                }
            }
        }

        ulong instanceMark = Time.GetTicksUsec();
        RelightIfDialMoved(lighting);
        _msInstances = SinceMs(ref instanceMark);
    }

    /// <summary>
    /// The moon phase the guest states (0..7, a frame of the eight-frame moon atlas), or the host
    /// default when it has not been read.
    /// </summary>
    private static int MoonPhaseNow =>
        Refii.Host.HostBridge.TimeOfDay() is { MoonPhase: >= 0 and <= 7 } clock
            ? clock.MoonPhase
            : Refii.Level.Engine_CelestialElements.DefaultMoonPhase;

    private CloudTheme? _cloudTheme;
    private SkyFrame? _frame;
    private ToneMapTheme? _toneMapTheme;
    private Engine_DynamicSkyDriver? _dynamicSky;
    public Engine_LandscapeGround? InteractionGround { get; set; }
    private Engine_DynamicSurfaceInteractions? _surfaceInteractions;
    private Engine_DynamicThunder? _dynamicThunder;
    private Engine_DynamicCloudShadows? _dynamicCloudShadowCaster;
    private Vector3 _lastMoon = new(float.NaN, 0, 0);

    private bool SkyIsDynamic =>
        Environment?.Sky?.SkyMaterial is ShaderMaterial { Shader: { } shader } &&
        shader.ResourcePath == Engine_DynamicSky.ShaderPath;

    private void FollowSkyChoice()
    {
        if (Theme is null || Environment?.Sky is not { } sky)
        {
            return;
        }

        bool wanted = Engine_DynamicSky.Enabled;
        if (wanted == SkyIsDynamic)
        {
            return;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);
        var shader = GD.Load<Shader>(
            wanted ? Engine_DynamicSky.ShaderPath : Engine_SkyEnvironment.ShaderPath);
        if (shader is null)
        {
            return;
        }

        if (wanted)
        {
            Engine_DynamicSky.EnsureGlobals();
        }

        var material = new ShaderMaterial { Shader = shader };
        Engine_SkyEnvironment.ApplyFrame(material, frame, Theme);
        if (wanted)
        {
            WeatherTheme weather = new();
            if (LightingGdbs.Count != 0)
            {
                Engine_EnvironmentTheme.TryExtractWeather(LightingGdbs, out weather);
            }

            Engine_DynamicSky.Prime(material, frame, Theme, weather);
        }

        sky.SkyMaterial = material;
        _dynamicSky = null;
        _dynamicThunder?.Bind(null);
        _lastMoon = new Vector3(float.NaN, 0, 0);

        Node? parent = GetParent();
        if (parent is not null)
        {
            if (parent.GetNodeOrNull<Node3D>(Path_Clouds) is { } clouds)
            {
                clouds.Visible = !(wanted && Engine_DynamicSky.HideGameClouds);
            }

            if (parent.GetNodeOrNull<Node3D>(Path_Celestial) is { } celestial)
            {
                celestial.Visible = !(wanted && Engine_DynamicSky.HideGameCelestial);
            }

            if (parent.GetNodeOrNull<Node3D>(Path_Weather) is { } weatherNode)
            {
                weatherNode.Visible = !(wanted && Engine_DynamicSky.HideGameWeather);
            }

            Engine_DynamicPrecipitation? precipitation =
                parent.GetNodeOrNull<Engine_DynamicPrecipitation>(Path_Precipitation);
            if (wanted && precipitation is null && Engine_DynamicPrecipitation.Build() is { } built)
            {
                parent.AddChild(built);
            }
            else if (!wanted && precipitation is not null)
            {
                precipitation.Visible = false;
            }
        }

        if (!wanted && Environment is not null)
        {
            Environment.FogEnabled = false;
        }

        GD.Print($"[dynamic-sky] live switch: sky is now {(wanted ? "dynamic" : "the Fable dome")}");
        Reevaluate();
    }

    private readonly float[] _cloudUvX = new float[4];
    private readonly float[] _cloudUvY = new float[4];
    private readonly Dictionary<int, (byte[] Alpha, int W, int H)> _cloudAlpha = new();

    private Godot.Compositor? _editorCompositor;
    private Camera3D? _editorCamera;

    private void PublishPostFx()
    {
        ToneMapTheme? live = LiveToneMap();
        Engine_PostFxParams.Publish(WithGuestThemes(live ?? _toneMapTheme, live is not null), Theme, _frame, ResolveCamera());
    }

    private readonly System.Collections.Generic.List<Refii.Host.HostBridge.GuestThemeLayer> _guestLayers = new();
    private readonly System.Collections.Generic.Dictionary<uint, ToneMapTheme?> _guestLayerThemes = new();
    private ToneMapTheme? _guestBlend;
    private ToneMapTheme? _guestScripted;

    /// <summary>
    /// The host's tone map with the themes the guest has PUSHED blended over it at the guest's
    /// own weights — Time Control's `EnvThemeSlowTime2` (Saturation -0.70, Brightness 0.25, its
    /// own Exposure and Contrast), spell charging, the banshee. The stack is the data point: the
    /// guest states each theme by name and the weight it is at this tick (in over 0.5 s, out
    /// over the spell's last 0.5 s for Time Control), and `sub_8218EAF8` composes them exactly
    /// so — the stack as one layer at `clamp(Σw, 0, 1)`, each theme at its share of Σw, each
    /// STATED slot lerped over the base. An empty stack returns the base untouched, so the
    /// picture at rest is unchanged. Fields a theme inherits through `parent` are not blended;
    /// only what the record and its sub-records state is.
    ///
    /// THE SCRIPT BLENDS GO FIRST, as their own layer: `sub_8218EAF8` folds the list
    /// `EnvironmentTheme.BlendToEnvironmentTheme` fills (`mgr+148`) after the day set and painted
    /// layers and before the stack (`mgr+164`), each list one group at `clamp(Σw)`. That list is
    /// the quests' looks — the Cursed Snowglobe's `PastMonochromeNoon` (Saturation -1,
    /// SaturationMasked +1), combat failure, XP, the balverine poison.
    ///
    /// THE CLASSIC BASE TAKES NEITHER. It is the render scene's theme block (scene+16), the copy
    /// of the record `sub_8218EAF8` folds — script list and stack included (read live in the
    /// Cursed Snowglobe: scene slot 44 = -1, the same as the record). Composing either again
    /// applied it twice.
    /// </summary>
    private ToneMapTheme? _liveToneMap;

    /// <summary>
    /// Under the classic renderer with the guest's theme block up: its tone-mapping parameters
    /// as the base, after the guest's own day-set blending: ToneMapParam 0..31 are theme indices
    /// 21..52 (exposure through radial blur) and the six depth-of-field ones 86..91. The pushed
    /// themes still compose over it in <see cref="WithGuestThemes"/>.
    /// </summary>
    private ToneMapTheme? LiveToneMap()
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || !Refii.Models.Engine_ClassicTheme.Live)
        {
            return null;
        }
        _liveToneMap ??= new ToneMapTheme { HasAny = true };
        for (int field = 0; field < ToneMapTheme.ParamCount; field++)
        {
            _liveToneMap.V[field] = Refii.Models.Engine_ClassicTheme.Param(field < 32 ? 21 + field : 86 + field - 32);
            _liveToneMap.Present[field] = true;
        }
        return _liveToneMap;
    }

    private ToneMapTheme? WithGuestThemes(ToneMapTheme? baseTheme, bool baseIsGuestFolded)
    {
        if (baseTheme is null || baseIsGuestFolded || Engine.IsEditorHint() || !Refii.Host.HostBridge.IsInGame)
        {
            return baseTheme;
        }

        Refii.Host.HostBridge.ThemeBlends(_guestLayers);
        _guestScripted ??= new ToneMapTheme();
        ToneMapTheme composed = ComposeGuestLayer(baseTheme, _guestLayers, _guestScripted);

        Refii.Host.HostBridge.ThemeStack(_guestLayers);
        _guestBlend ??= new ToneMapTheme();
        return ComposeGuestLayer(composed, _guestLayers, _guestBlend);
    }

    /// <summary>
    /// One guest group over <paramref name="baseTheme"/>, as `sub_82197938` folds it: the group
    /// at `clamp(Σw, 0, 1)`, its themes normalised by Σw, and each slot
    /// `dst·(1 − present·W) + src·W` (`sub_821D8588`). Returns the base itself when the group is
    /// empty, else <paramref name="into"/>.
    /// </summary>
    private ToneMapTheme ComposeGuestLayer(
        ToneMapTheme baseTheme, System.Collections.Generic.List<Refii.Host.HostBridge.GuestThemeLayer> layers,
        ToneMapTheme into)
    {
        float sum = 0f;
        foreach (Refii.Host.HostBridge.GuestThemeLayer layer in layers)
        {
            sum += Mathf.Max(layer.Weight, 0f);
        }

        if (sum <= 1e-4f)
        {
            return baseTheme;
        }

        float layerWeight = Mathf.Clamp(sum, 0f, 1f);
        into.CopyFrom(baseTheme);
        for (int field = 0; field < ToneMapTheme.ParamCount; field++)
        {
            float stated = 0f;
            float value = 0f;
            foreach (Refii.Host.HostBridge.GuestThemeLayer layer in layers)
            {
                if (layer.Weight <= 0f || GuestThemeFor(layer.RecordHash) is not { } theme || !theme.Present[field])
                {
                    continue;
                }

                float share = layer.Weight / sum;
                stated += share;
                value += share * theme.V[field];
            }

            if (stated > 0f)
            {
                into.V[field] = Mathf.Lerp(baseTheme.V[field], value / stated, layerWeight * stated);
                into.Present[field] = true;
            }
        }

        into.HasAny = true;
        return into;
    }

    /// <summary>One pushed theme's stated tone mapping, extracted once per record hash.</summary>
    private ToneMapTheme? GuestThemeFor(uint recordHash)
    {
        if (!_guestLayerThemes.TryGetValue(recordHash, out ToneMapTheme? theme))
        {
            theme = Engine_EnvironmentTheme.TryExtractToneMapRecord(LightingGdbs, recordHash, out ToneMapTheme stated)
                ? stated
                : null;
            _guestLayerThemes[recordHash] = theme;
            GD.Print(theme is null
                ? $"[postfx] guest pushed theme {recordHash:X8}: not in the lighting GDBs, not blended"
                : $"[postfx] guest pushed theme {recordHash:X8}: saturation={stated[ToneMapParam.Saturation]:0.###} brightness={stated[ToneMapParam.Brightness]:0.###} exposure={stated[ToneMapParam.MiddleGray]:0.###}");
        }

        return theme;
    }

    private void SyncEditorCompositor()
    {
        if (!Engine.IsEditorHint())
        {
            return;
        }

        Camera3D? camera = ResolveCamera();
        if (camera is null || !GodotObject.IsInstanceValid(camera))
        {
            return;
        }

        if (_editorCamera is not null && !ReferenceEquals(camera, _editorCamera))
        {
            DetachEditorCompositor();
        }

        _editorCompositor ??= Engine_LevelLighting.BuildCompositor();
        if (_editorCompositor is null)
        {
            return;
        }
        // The editor's view needs its depth pre-pass as the game's does; ownerless, so it is
        // never saved into the scene.
        if (GetParent() is Node3D level)
        {
            Engine_DepthPrepassMarker.Attach(level);
        }

        if (camera.Compositor != _editorCompositor)
        {
            camera.Compositor = _editorCompositor;
        }

        _editorCamera = camera;
    }

    private void DetachEditorCompositor()
    {
        if (_editorCamera is not null &&
            GodotObject.IsInstanceValid(_editorCamera) &&
            _editorCamera.Compositor == _editorCompositor)
        {
            _editorCamera.Compositor = null;
        }

        _editorCamera = null;
        _editorCompositor = null;
    }

    private Camera3D? ResolveCamera()
    {
        Camera3D? camera = GetViewport()?.GetCamera3D();
#if TOOLS
        if (camera is null && Engine.IsEditorHint())
        {
            camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }
#endif
        return camera;
    }

    private void AnimateSky(float delta)
    {
        Node3D? clouds = GetParent()?.GetNodeOrNull<Node3D>(Path_Clouds);
        CloudTheme? cloudTheme = _cloudTheme;
        if (clouds is not null && cloudTheme is not null)
        {
            AdvanceCloudLayers(clouds, cloudTheme, delta);
        }

        SkyFrame? frame = _frame;
        Node3D? celestial = GetParent()?.GetNodeOrNull<Node3D>(Path_Celestial);
        if (celestial is null || frame is null)
        {
            return;
        }

        Camera3D? camera = ResolveCamera();

        Vector3 right = camera?.GlobalTransform.Basis.X ?? Vector3.Right;
        Vector3 up = camera?.GlobalTransform.Basis.Y ?? Vector3.Up;

        if (celestial.GetNodeOrNull<MeshInstance3D>(Path_MoonGlare) is
                { MaterialOverride: ShaderMaterial moonGlare })
        {
            // The moon glare is a scene draw (celestial_overlay); the cloud term is its vis.
            Vector3 moonDir = Engine_SkyFrame.ToGodot(frame.MoonDirection);
            float vis = CloudVisibility(clouds, cloudTheme, moonDir, right, up);
            moonGlare.SetShaderParameter(Param_element_vis, vis);
        }

        if (celestial.GetNodeOrNull<MeshInstance3D>(Path_SunGlare) is
                { MaterialOverride: ShaderMaterial sunGlare })
        {
            Vector3 sunDir = Engine_SkyFrame.ToGodot(frame.SunDirection);
            float vis = CloudVisibility(clouds, cloudTheme, sunDir, right, up);
            sunGlare.SetShaderParameter(Param_element_vis, vis);
        }
    }

    private void AdvanceCloudLayers(Node3D clouds, CloudTheme theme, float delta)
    {
        // By index: GetChildren() is a Godot Array, a finalizable handle per frame.
        for (int i = 0, count = clouds.GetChildCount(); i < count; i++)
        {
            Node child = clouds.GetChild(i);
            if (child is not MeshInstance3D mesh ||
                mesh.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            string name = Refii.Core.Engine_NodeNames.Of(child);
            if (!name.StartsWith("CloudLayer", System.StringComparison.Ordinal) ||
                !int.TryParse(name.AsSpan("CloudLayer".Length), out int index) ||
                index < 0 || index >= theme.Layers.Length)
            {
                continue;
            }

            CloudLayerTheme layer = theme.Layers[index];
            // Under classic, the guest's own blended velocities: CLOUDS_LAYERn_VELOCITY_X/Y at
            // theme 145/146 + 14n, which `sub_82267610` scales by 0.001 per second.
            bool stated = Refii.Models.Engine_ClassicMaterial.Active && Refii.Models.Engine_ClassicTheme.Live;
            float velocityX = stated ? Refii.Models.Engine_ClassicTheme.Param(145 + 14 * index) : layer.VelocityX;
            float velocityY = stated ? Refii.Models.Engine_ClassicTheme.Param(146 + 14 * index) : layer.VelocityY;
            _cloudUvX[index] = WrapUnit(
                _cloudUvX[index] +
                (velocityX * Engine_Clouds.VelocityScale * delta));
            _cloudUvY[index] = WrapUnit(
                _cloudUvY[index] +
                (velocityY * Engine_Clouds.VelocityScale * delta));

            Vector4 current = material.GetShaderParameter(Param_uv_scale_offset).AsVector4();
            material.SetShaderParameter(Param_uv_scale_offset, new Vector4(
                current.X, current.Y, _cloudUvX[index], _cloudUvY[index]));
            if (material.HasMeta(Engine_ClassicClouds.ClassicCloudMeta))
            {
                Engine_ClassicClouds.SetScroll(material, _cloudUvX[index], _cloudUvY[index]);
            }
        }
    }

    private static float WrapUnit(float value)
    {
        value -= Mathf.Floor(value);
        return float.IsFinite(value) ? value : 0f;
    }

    private float CloudVisibility(
        Node3D? clouds, CloudTheme? theme, Vector3 direction, Vector3 right, Vector3 up)
    {
        if (clouds is null || theme is null || direction.Y <= 0.02f)
        {
            return 1.0f;
        }

        Span<Vector2> offsets = stackalloc Vector2[]
        {
            new Vector2(0f, 0f), new Vector2(0.03f, 0f), new Vector2(-0.03f, 0f),
            new Vector2(0f, 0.03f), new Vector2(0f, -0.03f),
        };

        float visSum = 0f;
        foreach (Vector2 offset in offsets)
        {
            Vector3 d = direction + (right * offset.X) + (up * offset.Y);
            float vis = 1f;
            for (int i = 0; i < theme.Layers.Length && i < 4; i++)
            {
                CloudLayerTheme cfg = theme.Layers[i];
                if (cfg.Transparency <= 0.001f || cfg.SizeX <= 1f || cfg.SizeY <= 1f ||
                    d.Y <= 0.02f)
                {
                    continue;
                }

                if (!TryCloudAlpha(clouds, i, out (byte[] Alpha, int W, int H) map))
                {
                    continue;
                }

                float s = cfg.Height / d.Y;
                if (s <= 0f)
                {
                    continue;
                }

                float hx = d.X * s;
                float hz = d.Z * s;
                float dist = Mathf.Sqrt((hx * hx) + (hz * hz));
                float fade = 1f - Mathf.Clamp((dist - 1000f) * 0.001f, 0f, 1f);
                if (fade <= 0f)
                {
                    continue;
                }

                float u = (((hx + cfg.SizeX) / (2f * cfg.SizeX)) * cfg.TextureScaleX) +
                          _cloudUvX[i];
                float v = (((hz + cfg.SizeY) / (2f * cfg.SizeY)) * cfg.TextureScaleY) +
                          _cloudUvY[i];
                float a = SampleAlphaBilinear(map.Alpha, map.W, map.H, u, v);
                float coverage = Mathf.Clamp(a * cfg.Transparency * fade, 0f, 1f);
                vis *= 1f - coverage;
            }

            visSum += vis;
        }

        return Mathf.Clamp(visSum / 5f, 0f, 1f);
    }

    private bool TryCloudAlpha(
        Node3D clouds, int index, out (byte[] Alpha, int W, int H) map)
    {
        if (_cloudAlpha.TryGetValue(index, out map))
        {
            return map.Alpha.Length != 0;
        }

        map = (System.Array.Empty<byte>(), 0, 0);
        if (clouds.GetNodeOrNull<MeshInstance3D>($"CloudLayer{index}") is not
                { MaterialOverride: ShaderMaterial material })
        {
            _cloudAlpha[index] = map;
            return false;
        }

        var texture = material.GetShaderParameter(Param_cloud_density).As<Texture2D>();

        // The map's decoded image, kept by Engine_Clouds when it made the texture: reading it
        // here never crosses to the render thread. Texture2D.GetImage does, synchronously, and
        // on the first frame after an area streams in it waits for every queued upload. A
        // texture Engine_Clouds did not make still reads back, as before.
        Image? source = Engine_Clouds.DensitySource(texture);
        bool verify = CloudVerifyEnabled;
        Image? fromServer = verify || source is null ? texture?.GetImage() : null;

        // The source is shared and the upload may still be reading it: change a copy.
        Image? image = verify ? fromServer : source is not null ? (Image)source.Duplicate() : fromServer;
        if (image is null)
        {
            _cloudAlpha[index] = map;
            return false;
        }

        map = AlphaMap(image);
        _cloudAlpha[index] = map;

        if (verify && source is not null)
        {
            (byte[] Alpha, int W, int H) mine = AlphaMap((Image)source.Duplicate());
            bool same = mine.W == map.W && mine.H == map.H &&
                        mine.Alpha.AsSpan().SequenceEqual(map.Alpha);
            GD.Print($"[sky-clouds] layer {index}: VERIFY {(same ? "identical" : "MISMATCH")} " +
                     $"(server {map.W}x{map.H}, source {mine.W}x{mine.H}, " +
                     $"{Engine_CloudAlpha.Differences(mine.Alpha, map.Alpha)} texel(s) differ)");
        }
        else
        {
            GD.Print($"[sky-clouds] layer {index}: alpha map {map.W}x{map.H} " +
                     (source is not null ? "from the decoded source (no server read)" : "READ BACK from the server (no source kept)"));
        }

        // As before: the first look answers true even for an empty map; later looks, from the
        // cache above, answer by its length.
        return true;
    }

    /// <summary>The image's alpha channel, texel 0 of mip 0 first. Changes <paramref name="image"/>.</summary>
    private static (byte[] Alpha, int W, int H) AlphaMap(Image image)
    {
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        // One native crossing for the whole map, not a GetPixel per texel.
        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        int w = image.GetWidth();
        int h = image.GetHeight();
        return (Engine_CloudAlpha.Channel(image.GetData(), w * h), w, h);
    }

    private static int _cloudVerify = -1;

    /// <summary>
    /// VERIFY MODE, for validation only: a file named <c>refii_cloud_verify.flag</c> beside the
    /// executable. The sky then reads each map back from the server as the old code did, uses
    /// that, and logs whether the decoded source gives the same alpha byte for byte.
    /// </summary>
    private static bool CloudVerifyEnabled
    {
        get
        {
            if (_cloudVerify < 0)
            {
                bool on = false;
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
                    on = System.IO.File.Exists(System.IO.Path.Combine(dir, "refii_cloud_verify.flag"));
                }
                catch (Exception)
                {
                    on = false;
                }

                _cloudVerify = on ? 1 : 0;
                GD.Print($"[sky-clouds] density alpha from the decoded source; verify mode {(on ? "ON (old server read kept, results compared)" : "off")}");
            }

            return _cloudVerify == 1;
        }
    }

    private static float SampleAlphaBilinear(byte[] alpha, int w, int h, float u, float v)
    {
        if (alpha.Length == 0 || w <= 0 || h <= 0)
        {
            return 0f;
        }

        u -= Mathf.Floor(u);
        v -= Mathf.Floor(v);
        float fx = (u * w) - 0.5f;
        float fy = (v * h) - 0.5f;
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        float Tap(int x, int y)
        {
            x %= w;
            if (x < 0)
            {
                x += w;
            }

            y %= h;
            if (y < 0)
            {
                y += h;
            }

            return alpha[(y * w) + x] / 255f;
        }

        float a00 = Tap(x0, y0);
        float a10 = Tap(x0 + 1, y0);
        float a01 = Tap(x0, y0 + 1);
        float a11 = Tap(x0 + 1, y0 + 1);
        return (((a00 * (1f - tx)) + (a10 * tx)) * (1f - ty)) +
               (((a01 * (1f - tx)) + (a11 * tx)) * ty);
    }

    private bool EnsureTheme()
    {
        if (Theme is not null && LightingGdbs.Count != 0)
        {
            return true;
        }

        List<byte[]> chain = Engine_SkyEnvironment.BuildGdbChain(
            LevelGdbPath, Engine_BnkMeshLoader.ReadBytes);
        if (chain.Count == 0)
        {
            return Theme is not null;
        }

        if (Theme is null)
        {
            if (!Engine_EnvironmentTheme.TryExtractSky(chain, out SkyTheme sky))
            {
                return false;
            }

            Theme = sky;
        }

        if (LightingGdbs.Count == 0)
        {
            LightingGdbs.AddRange(chain);
        }

        return true;
    }

    private DirectionalLight3D? ResolveSun()
    {
        if (Sun is not null && IsInstanceValid(Sun))
        {
            return Sun;
        }

        Sun = GetParent()?.GetNodeOrNull<DirectionalLight3D>(Path_Sun);
        return Sun;
    }
}
