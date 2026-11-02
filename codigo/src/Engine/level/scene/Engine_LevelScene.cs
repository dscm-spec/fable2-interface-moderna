using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;

namespace Refii.Level;

public static class Engine_LevelMesh
{
    /// <summary>
    /// Cells per side of a terrain chunk: 64 cells = 32 m. Godot culls per instance, so as one
    /// mesh the whole heightfield is drawn by every pass whose frustum touches its box — the
    /// pre-pass, the opaque pass, each sun cascade and each mirror; as chunks each pass draws
    /// the chunks it sees. The chunks share the grid's border vertices and the one splat material.
    /// </summary>
    public const int ChunkCells = 64;

    public static ArrayMesh ToArrayMesh(TerrainMesh terrain, string surfaceName)
    {
        int count = terrain.VertexCount;
        var vertexIds = new int[count];
        for (int v = 0; v < count; v++)
        {
            vertexIds[v] = v;
        }

        var mesh = new ArrayMesh();
        AddSurface(mesh, terrain, vertexIds, terrain.Indices, surfaceName);
        return mesh;
    }

    /// <summary>
    /// The grid as <see cref="ChunkCells"/>-cell chunks, row-major, each a mesh of its own with
    /// the surface named after the chunk. A grid smaller than one chunk is one chunk.
    /// </summary>
    public static List<ArrayMesh> ToChunkMeshes(TerrainMesh terrain, string name)
    {
        var chunks = new List<ArrayMesh>();
        int w = terrain.Width;
        int h = terrain.Height;
        if (w < 2 || h < 2)
        {
            return chunks;
        }

        int columns = Math.Max(1, (w - 1 + ChunkCells - 1) / ChunkCells);
        int rows = Math.Max(1, (h - 1 + ChunkCells - 1) / ChunkCells);
        var local = new int[(long)w * h];
        for (int cy = 0; cy < rows; cy++)
        {
            int y0 = cy * ChunkCells;
            int y1 = Math.Min(h - 1, y0 + ChunkCells);
            for (int cx = 0; cx < columns; cx++)
            {
                int x0 = cx * ChunkCells;
                int x1 = Math.Min(w - 1, x0 + ChunkCells);
                int chunkW = x1 - x0 + 1;
                int chunkH = y1 - y0 + 1;
                var vertexIds = new int[chunkW * chunkH];
                int n = 0;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int id = (y * w) + x;
                        local[id] = n;
                        vertexIds[n++] = id;
                    }
                }

                var indices = new int[(chunkW - 1) * (chunkH - 1) * 6];
                int k = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        // The grid's winding (Engine_GhfTerrain.BuildMesh): clockwise from above
                        // under the region's mirrored root.
                        int i00 = local[(y * w) + x];
                        int i10 = local[(y * w) + x + 1];
                        int i01 = local[((y + 1) * w) + x];
                        int i11 = local[((y + 1) * w) + x + 1];
                        indices[k++] = i00;
                        indices[k++] = i01;
                        indices[k++] = i10;
                        indices[k++] = i10;
                        indices[k++] = i01;
                        indices[k++] = i11;
                    }
                }

                var mesh = new ArrayMesh();
                AddSurface(mesh, terrain, vertexIds, indices, $"{name}[{cx},{cy}]");
                chunks.Add(mesh);
            }
        }

        return chunks;
    }

    /// <summary>
    /// One surface from the grid vertices <paramref name="vertexIds"/> name, indexed by
    /// <paramref name="indices"/> into that list.
    /// </summary>
    private static void AddSurface(
        ArrayMesh mesh, TerrainMesh terrain, int[] vertexIds, int[] indices, string surfaceName)
    {
        int count = vertexIds.Length;
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        var tangents = new float[count * 4];

        // THE TANGENT FRAME, WITHOUT WHICH THE NORMAL MAPS DO NOTHING. The splat shader writes
        // NORMAL_MAP, and Godot applies that through TANGENT and BINORMAL — with no tangent
        // array the frame degenerates and the perturbed normal collapses back to the geometric
        // one, so every layer normal map was decoded, blended and then thrown away before
        // light() ran. The frame is analytic because the projection is: u runs along local +X
        // and v along local -Z (terrain_splat's ground_uv), so the tangent is +X everywhere,
        // and w = +1 makes Godot's `cross(NORMAL, TANGENT) * w` come out -Z to match v.
        //
        // THE GUEST'S OWN FRAME (landscape VS, blob 0173): T = Gram-Schmidt(+X), B = T x n =
        // guest -Y, and its ground V negated (v = -worldY). Our local Z is guest Y, so this is
        // that frame exactly; v and B disagreeing is the broken combination.
        for (int n = 0; n < count; n++)
        {
            int v = vertexIds[n];
            vertices[n] = new Vector3(
                terrain.Positions[(v * 3) + 0],
                terrain.Positions[(v * 3) + 1],
                terrain.Positions[(v * 3) + 2]);

            var normal = new Vector3(
                terrain.Normals[(v * 3) + 0],
                terrain.Normals[(v * 3) + 1],
                terrain.Normals[(v * 3) + 2]);
            normals[n] = normal;
            uvs[n] = new Vector2(terrain.Uvs[(v * 2) + 0], terrain.Uvs[(v * 2) + 1]);

            // GRAM-SCHMIDT PER VERTEX, exactly the guest's own construction (blob 0173 builds
            // T = Gram-Schmidt(+X) against the vertex normal). A constant (1, 0, 0) is NOT
            // orthogonal to a sloped normal, and Godot's compressed vertex format encodes the
            // normal+tangent PAIR on the assumption that it is — a non-orthogonal tangent
            // corrupts the shared encoding, which surfaced as garbage view-space normals on
            // every mirrored terrain instance (the level root's Scale(-1,1,1)) and killed the
            // ground's NdotL for every light in the game.
            Vector3 tangent = new Vector3(1f, 0f, 0f) - (normal * normal.X);
            tangent = tangent.LengthSquared() > 1e-8f ? tangent.Normalized() : new Vector3(1f, 0f, 0f);
            tangents[(n * 4) + 0] = tangent.X;
            tangents[(n * 4) + 1] = tangent.Y;
            tangents[(n * 4) + 2] = tangent.Z;
            tangents[(n * 4) + 3] = 1f;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(mesh.GetSurfaceCount() - 1, surfaceName);
    }
}

public static class Engine_LevelScene
{
    public const string TerrainNodeName = "Terrain";

    public const string AdjacentNodeName = "AdjacentTerrain";

    /// <summary>The chunk instances of the region's terrain, in grid order; empty without one.</summary>
    public static List<MeshInstance3D> TerrainChunksOf(Node region)
    {
        var chunks = new List<MeshInstance3D>();
        if (region.GetNodeOrNull<Node3D>(TerrainNodeName) is not { } container)
        {
            return chunks;
        }

        foreach (Node child in container.GetChildren())
        {
            if (child is MeshInstance3D { Mesh: not null } chunk)
            {
                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    public const string WaterNodeName = "Water";

    /// <summary>The water node's path held once; a string makes a finalizable NodePath per lookup.</summary>
    public static readonly NodePath WaterNodePath = WaterNodeName;

    /// <summary>Allows testing the Editor-specific build pipeline in headless CLI runs.</summary>
    public static bool ForceEditorMode { get; set; }

    public static bool TryBuild(
        LevelEntry level, Func<string, byte[]?>? source, out string scenePath, out string error) =>
        TryBuild(level, source, null, out scenePath, out error);

    /// <summary>
    /// Builds a level and SAVES it as a scene — the editor's path.
    ///
    /// Saving is an editor capability, not a game one: <c>res://</c> is read-only inside an
    /// exported pack, so this only works while running from the project. The GAME calls
    /// <see cref="Build"/> and mounts the node directly, which is the same construction and one
    /// less round trip through the disk.
    /// </summary>
    public static bool TryBuild(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        out string scenePath,
        out string error)
    {
        scenePath = level.ScenePath;

        Refii.Models.Engine_MdlMaterial.WarmShaders();
        Refii.Entities.Engine_ShadowProxy.WarmShader();
        _ = GD.Load<Shader>(Engine_TerrainSplatMaterial.ShaderPath);

        if (!EnsureDirectory(level, out error))
        {
            return false;
        }

        if (Build(level, source, findByName, out error) is not { } root)
        {
            return false;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        var packed = new PackedScene();
        Error packedResult = packed.Pack(root);
        if (packedResult != Error.Ok)
        {
            root.QueueFree();
            error = $"could not pack scene: {packedResult}";
            return false;
        }

        long packMs = clock.ElapsedMilliseconds;
        clock.Restart();

        Error saved = ResourceSaver.Save(packed, level.ScenePath);
        root.QueueFree();

        if (saved != Error.Ok)
        {
            error = $"could not save scene to {level.ScenePath}: {saved}";
            return false;
        }

        long size = 0;
        string globalized = ProjectSettings.GlobalizePath(level.ScenePath);
        if (System.IO.File.Exists(globalized))
        {
            size = new System.IO.FileInfo(globalized).Length;
        }

        GD.Print(
            $"[level-save] {level}: pack {packMs} ms, save {clock.ElapsedMilliseconds} ms, " +
            $"{size / (1024.0 * 1024.0):0.0} MB at {level.ScenePath}");

        return true;
    }

    /// <summary>
    /// Builds a level and hands back the live node. THE CALLER OWNS IT.
    ///
    /// This is the whole construction — terrain, water, props, entities, lighting, sky, weather
    /// — and it is the one implementation the editor and the game share. The editor packs and
    /// saves what comes back; the game adds it to the tree.
    ///
    /// THE GAME USED TO GO THROUGH THE DISK: build, `ResourceSaver.Save` to `res://levels/...`,
    /// then `ResourceLoader.Load` and instantiate. That only ever worked because we run from
    /// the editor — `res://` is inside a read-only pack in an exported build, so the save fails
    /// and the game gets no world. Nothing about streaming a region needs a `.tscn` to exist;
    /// the banks are read in place and the props page in over frames either way.
    ///
    /// Children still get their <c>Owner</c> set, because that is what makes them survive the
    /// editor's pack and it costs the game nothing.
    /// </summary>
    /// <summary>
    /// The level's painted day-set grid, or null when it paints nothing. The grid is the
    /// heightfield's sibling with the .genv extension (the market: bwsmainheightfield_id_….genv
    /// beside ….ghf), spans the heightfield from its origin, and the guest loads every .genv the
    /// manifest lists — a region has one per heightfield that carries paint.
    /// </summary>
    private static Refii.Lighting.Engine_PaintedDaySets? LoadPaintedDaySets(
        LevelEntry level, StatedWorld stated, Func<string, byte[]?> source, IReadOnlyList<byte[]> gdbs)
    {
        var painted = new Refii.Lighting.Engine_PaintedDaySets();
        var sets = new Dictionary<uint, int>();
        foreach (StatedLandscape landscape in stated.Landscapes)
        {
            if (landscape.Ghf.Length == 0)
            {
                continue;
            }

            string genvPath = Engine_LevelGdb.SiblingWithExtension(landscape.Ghf, ".genv");
            if (source(genvPath) is not { } bytes)
            {
                continue;
            }

            if (source(landscape.Ghf) is not { } ghfBytes ||
                !Engine_GhfTerrain.TryDecode(ghfBytes, out GhfHeights heights, out _))
            {
                GD.Print($"[painted] {level.Name}: {genvPath} present but the heightfield did not decode; no origin");
                continue;
            }

            if (!Refii.Lighting.Engine_PaintedDaySets.TryParse(
                    bytes, heights.OriginX, heights.OriginZ, out Refii.Lighting.Engine_PaintedDaySets.Grid grid, out string error))
            {
                GD.Print($"[painted] {level.Name}: {genvPath}: {error}");
                continue;
            }

            int before = painted.PaintedCells;
            painted.Add(grid);
            foreach (uint cell in grid.Cells)
            {
                if (cell != Refii.Lighting.Engine_PaintedDaySets.None)
                {
                    sets[cell] = sets.TryGetValue(cell, out int n) ? n + 1 : 1;
                }
            }

            GD.Print($"[painted] {level.Name}: {genvPath}: {grid.Width}x{grid.Height} cells at {grid.Cell} m from " +
                $"({grid.OriginX:0.#}, {grid.OriginZ:0.#}), {painted.PaintedCells - before} painted");
        }

        if (painted.PaintedCells == 0)
        {
            return null;
        }

        Engine_LightingTheme.ReadPaintedBlendTimes(gdbs, out painted.BlendInSeconds, out painted.BlendOutSeconds);
        var parts = new List<string>();
        foreach ((uint hash, int count) in sets)
        {
            parts.Add($"0x{hash:X8}x{count}");
        }

        GD.Print($"[painted] {level.Name}: {painted.PaintedCells} painted: {string.Join(" ", parts)}; " +
            $"blend in/out {painted.BlendInSeconds:0.#}/{painted.BlendOutSeconds:0.#} s");
        return painted;
    }

    /// <summary>The scenario's own heightfield: the discovered one when the .list names it, else its first .ghf.</summary>
    private static string StatedHeightfield(
        LevelEntry level, ScenarioManifest manifest, Func<string, byte[]?> read, string discovered)
    {
        if (manifest.Ghf.Count == 0 ||
            manifest.Ghf.Exists(line => Engine_LevelManifest.VirtualPath(line).Equals(
                discovered, StringComparison.OrdinalIgnoreCase)) ||
            !Engine_LevelManifest.TryResolvePath(manifest.Ghf[0], read, out string stated))
        {
            return discovered;
        }

        GD.Print(
            $"[level] {level}: manifest states heightfield {stated}" +
            (discovered.Length != 0 ? $" (discovery had {discovered})" : string.Empty));
        return stated;
    }

    /// <summary>
    /// What a level's stated landscapes and water come to, without building it: per landscape
    /// whether its grid decodes and what material it takes (and so whether it is drawn); per
    /// water file how many bodies are entirely under the ground; the ocean's height and how much
    /// of the mapped ground lies below it. One line per finding.
    /// </summary>
    /// <summary>The .ehf of every stated landscape with no stated grid — the vistas.</summary>
    public static List<string> VistaEhfPaths(LevelEntry level, Func<string, byte[]?> source)
    {
        var paths = new List<string>();
        if (Engine_LevelManifest.TryResolve(level, source, out ScenarioManifest manifest, out _, out _))
        {
            string heightfield = StatedHeightfield(level, manifest, source, level.HeightfieldPath);
            foreach (StatedLandscape landscape in ReadStatedWorld(level, manifest, source, heightfield).Landscapes)
            {
                if (landscape.Ghf.Length == 0 && landscape.Ehf.Length != 0)
                {
                    paths.Add(landscape.Ehf);
                }
            }
        }

        return paths;
    }

    public static List<string> Audit(LevelEntry level, Func<string, byte[]?> source)
    {
        var lines = new List<string>();
        Engine_LevelVfsConfig.Apply(level, source);
        if (!Engine_LevelManifest.TryResolve(level, source, out ScenarioManifest manifest, out _, out string scenario))
        {
            lines.Add($"{level}: no scenario .list");
            return lines;
        }

        if (level.Scenario.Length == 0)
        {
            level.Scenario = scenario;
        }

        string heightfield = StatedHeightfield(level, manifest, source, level.HeightfieldPath);
        StatedWorld stated = ReadStatedWorld(level, manifest, source, heightfield);
        List<(StatedLandscape Landscape, GhfHeights? Grid)> grids = DecodeGrids(stated, source);
        Engine_LandscapeGround ground = GroundOf(grids);

        // Each landscape's bake, decoded as the binder decodes it, summarised by its mean colour.
        var bakes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Engine_LevelGdb.TryRead(level, p => source(p), out string gdbPath);
        Engine_LevelBakedLighting.TryLoad(gdbPath, p => source(p), out LightmapContainer? lightmaps, out _);
        string enginePath = Engine_LevelGdb.EngineLevelPath(level, source, gdbPath);
        if (lightmaps is not null && (source(enginePath) ?? source("data/" + enginePath)) is { } engineBytes)
        {
            foreach (EngineLevelHeightfield record in Engine_EngineLevelFile.Parse(engineBytes).Heightfields)
            {
                string bake = "no bake";
                if (Engine_LevelBakedLighting.TryGetTerrainLightmap(lightmaps, record.LightmapKey, out TerrainLightmap lm))
                {
                    ImageTexture? texture = Engine_LevelBakedLighting.DecodeTexture(lm.Blob);
                    if (texture?.GetImage() is { } image)
                    {
                        if (image.IsCompressed())
                        {
                            image.Decompress();
                        }

                        double r = 0, g = 0, b = 0;
                        int n = 0;
                        for (int y = 0; y < image.GetHeight(); y += 2)
                        {
                            for (int x = 0; x < image.GetWidth(); x += 2)
                            {
                                Color c = image.GetPixel(x, y);
                                r += c.R; g += c.G; b += c.B; n++;
                            }
                        }

                        bake = $"bake {image.GetWidth()}x{image.GetHeight()} {image.GetFormat()} mean " +
                            $"({r / n:0.00},{g / n:0.00},{b / n:0.00})";
                    }
                    else
                    {
                        bake = "bake DID NOT DECODE";
                    }
                }

                bakes[StemOf(record.Path)] = bake;
            }
        }

        foreach ((StatedLandscape landscape, GhfHeights? grid) in grids)
        {
            bool isGrid = landscape.Ghf.Length != 0;
            Material? material = landscape.Ehf.Length != 0 && isGrid ? LoadTerrainMaterial(landscape.Ehf, source, null) : null;
            Engine_EhfPages.Built? pagedBuild = null;
            bool pagedOk = !isGrid && grid is not null && landscape.Ehf.Length != 0 &&
                source(landscape.Ehf) is { } pageBytes &&
                Engine_EhfPages.TryBuild(pageBytes, grid, out pagedBuild, out _);
            if (!isGrid && !pagedOk && landscape.Ehf.Length != 0)
            {
                material = LoadTerrainMaterial(landscape.Ehf, source, null, vista: true);
            }

            bool drawn = grid is not null && (isGrid || pagedOk || material is not null);
            string what = isGrid ? "grid" : "vista";
            string size = grid is null ? "NO HEIGHTS" :
                $"{grid.Width}x{grid.Height}@{grid.Spacing} at ({grid.OriginX:0.#},{grid.OriginZ:0.#})..(" +
                $"{grid.OriginX + ((grid.Width - 1) * grid.Spacing):0.#},{grid.OriginZ + ((grid.Height - 1) * grid.Spacing):0.#}) " +
                $"h {grid.MinHeight:0.#}..{grid.MaxHeight:0.#}";
            string palette = string.Empty;
            if (!isGrid && landscape.Ehf.Length != 0 && source(landscape.Ehf) is { } ehfBytes &&
                Engine_EhfBody.TryParse(ehfBytes, out EhfParsedBody body, out _))
            {
                palette = " palette[" + string.Join(", ", body.Lods.ConvertAll(l => System.IO.Path.GetFileName(l.BaseDiffuse))) +
                    $"] pages {body.BackgroundPatches.Count} patch(es)";
            }

            lines.Add($"{level}: {(drawn ? "ok     " : "MISSING")} {what} {StemOf(landscape.Ehf.Length != 0 ? landscape.Ehf : landscape.Ghf)}{palette} " +
                $"{size} {(pagedOk ? $"COLOUR PAGES on {pagedBuild!.Chunks}/{pagedBuild.GridChunks} chunks" : material is null ? "NO MATERIAL" : material.GetClass())} " +
                (bakes.TryGetValue(StemOf(landscape.Ehf), out string? bakeText) ? bakeText : "no record"));
        }

        foreach (string line in manifest.Ghf)
        {
            if (Engine_LevelManifest.TryResolvePath(line, source, out string ghf) &&
                !stated.Landscapes.Exists(l => l.Ghf.Equals(ghf, StringComparison.OrdinalIgnoreCase)))
            {
                // The .list loads every .ghf (gameplay ground); only type-4 records draw, so the
                // guest draws none of these either — another scenario's landscape.
                lines.Add($"{level}: note   .list grid {ghf} is ground only (no .engine_level landscape draws it)");
            }
        }

        float oceanHeight = float.PositiveInfinity;
        foreach (string waterPath in stated.Water)
        {
            if (source(waterPath) is not { Length: > 8 } bytes ||
                !Engine_WaterFile.TryParse(bytes, out WaterScene scene, out _) || scene.Bodies.Count == 0)
            {
                continue;
            }

            int under = 0;
            float lowest = float.PositiveInfinity;
            foreach (WaterBody body in scene.Bodies)
            {
                lowest = MathF.Min(lowest, body.BaseHeight);
                if (ground.GridCount != 0 && ground.OpenCells(body, true, out _) == 0)
                {
                    under++;
                }
            }

            if (!float.IsFinite(oceanHeight))
            {
                oceanHeight = lowest - 0.1f;
            }

            lines.Add($"{level}: water {StemOf(waterPath)}: {scene.Bodies.Count} body(s), {under} entirely under the ground");
        }

        if (float.IsFinite(oceanHeight))
        {
            if (!AllowsVistaOcean(level))
            {
                lines.Add($"{level}: inland level ({level.LevelPath}) — vista ocean not applicable");
            }
            else
            {
                // The theme's switch across the day: the ocean draws only where it reads exactly 0.
                List<byte[]> gdbs = Engine_SkyEnvironment.BuildGdbChain(level, p => source(p));
                var gate = new List<string>();
                foreach (float tod in new[] { 0.0f, 0.25f, 0.5f, 0.75f })
                {
                    gate.Add(gdbs.Count != 0 &&
                        Engine_ThemeTimeline.TryExtractWaterBlended(gdbs, tod, out WaterTheme water)
                        ? (water.Stated[WaterThemeParam.DisableVistaWater]
                            ? water[WaterThemeParam.DisableVistaWater].ToString("0.##")
                            : "unstated")
                        : "no theme");
                }

                lines.Add($"{level}: ocean DisableVistaWater at 0/6/12/18h: {string.Join(" ", gate)}");

                int samples = 0;
                int above = 0;
                foreach ((_, GhfHeights? grid) in grids)
                {
                    if (grid is null)
                    {
                        continue;
                    }

                    foreach (float h in grid.Values)
                    {
                        samples++;
                        if (h < oceanHeight)
                        {
                            above++;
                        }
                    }
                }

                lines.Add($"{level}: ocean at {oceanHeight:0.###}: above the ground on " +
                    $"{(samples == 0 ? 0 : 100.0 * above / samples):0.#}% of mapped samples");
            }
        }

        return lines;
    }

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        out string error)
    {
        error = string.Empty;

        // PER-PHASE TIMING. It is what settled where the cost actually was: 14312 ms total,
        // of which terrain 9357 and props 3634 — so the answer was never "split the small
        // phases across frames", it was to get the whole thing off the main thread.
        //
        // THE GAME NOW CALLS THIS FROM A WORKER (see `GameWorld.StartBuild`); the editor still
        // calls it inline. Nothing here may touch Godot's scene tree: every node this builds is
        // detached until the caller mounts it, and `AddChild`/`Owner` on a detached subtree are
        // plain object writes rather than tree operations. Keep it that way — a `GetTree()`,
        // an autoload lookup or a `MakeCurrent` added here would fault only in the game, only
        // sometimes, and would look like a level-data bug.
        //
        // The phase numbers are still worth keeping. They are now CPU time on the worker rather
        // than stall time on the main loop, which is a different quantity with the same units.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phases = new List<(string Name, long Ms)>();
        long phaseStart = 0;
        void Mark(string name)
        {
            long now = clock.ElapsedMilliseconds;
            phases.Add((name, now - phaseStart));
            phaseStart = now;
        }

        LevelReads? reads = null;
        if (source is not null)
        {
            reads = new LevelReads(source);
            source = reads.Read;
            Refii.Animations.Engine_AnimCatalog.Prewarm();
        }

        string heightfieldPath = level.HeightfieldPath;
        ScenarioManifest? gameManifest = null;
        if (source is not null)
        {
            Engine_LevelVfsConfig.Apply(level, source);

            if (Engine_LevelManifest.TryResolve(
                    level, source, out ScenarioManifest manifest, out string manifestPath,
                    out string scenarioUsed))
            {
                gameManifest = manifest;
                if (level.Scenario.Length == 0)
                {
                    level.Scenario = scenarioUsed;
                }

                GD.Print(
                    $"[level] LoadLevel world={level.World} level={level.Name} " +
                    $"scenario={scenarioUsed} " +
                    $"id=0x{Engine_LevelManifest.LevelId(level.Name):X8} — " +
                    $"{manifestPath}: {manifest.Describe()}");
            }
            else
            {
                GD.Print($"[level] {level}: no scenario .list, using discovered heightfield");
            }
        }

        Mark("manifest");

        if (gameManifest is not null && reads is not null)
        {
            Preload(level, gameManifest, reads);
            Mark("preload");

            heightfieldPath = StatedHeightfield(level, gameManifest, reads.Read, heightfieldPath);

            ReadGameOrderResources(level, gameManifest, reads.Read);
            Mark("game-order");
        }

        if (heightfieldPath.Length == 0)
        {
            error = "level has no heightfield";
            return null;
        }

        string meshPath = Engine_BnkTerrainLoader.PathPrefix + heightfieldPath;
        StatedWorld stated = ReadStatedWorld(level, gameManifest, source, heightfieldPath);
        Mark("engine-level");

        Func<string, byte[]?>? terrainRead = source;
        var interactionGround = new Engine_LandscapeGround();
        List<BuiltLandscape> TerrainWork()
        {
            // Every landscape the .engine_level states is built: the guest gives each type-4
            // record its own landscape entity (sub_82B36478, vtable off_82005870) through one
            // path with no main/vista branch. One whose .ghf the scenario .list names is the
            // full grid the `game://` loader builds, cut into chunks (Engine_LevelMesh.ChunkCells);
            // one without a .ghf is built from the .ehf's own patches.
            var built = new List<BuiltLandscape>();
            int vistas = 0;
            foreach (StatedLandscape landscape in stated.Landscapes)
            {
                bool main = built.Count == 0;

                // A vista's material is its fallback only; the colour pages come first.
                Material? material = landscape.Ehf.Length != 0 && landscape.Ghf.Length != 0
                    ? LoadTerrainMaterial(landscape.Ehf, source, findByName)
                    : null;

                if (landscape.Ghf.Length != 0)
                {
                    var chunks = new List<ArrayMesh>();
                    string ghfError = terrainRead is null ? "no reader" : "not in any bank";
                    string name = main ? TerrainNodeName : $"{TerrainNodeName}_{StemOf(landscape.Ghf)}_";
                    if (terrainRead?.Invoke(landscape.Ghf) is { } ghfBytes &&
                        Engine_GhfTerrain.TryDecode(ghfBytes, out GhfHeights heights, out ghfError))
                    {
                        interactionGround.Add(heights);
                        // Under the classic renderer the main landscape is the title's own
                        // patches on its own shaders, each patch's layers as its materials.
                        string classicError = "no .ehf";
                        List<ArrayMesh>? patches = null;
                        if (Refii.Models.Engine_ClassicMaterial.Active && landscape.Ehf.Length != 0 &&
                            terrainRead(landscape.Ehf) is { } classicEhf &&
                            !Refii.Level.Engine_ClassicTerrain.TryBuild(classicEhf, heights, name, out patches, out classicError))
                        {
                            patches = null;
                        }
                        if (patches is not null)
                        {
                            chunks = patches;
                            material = null;
                            GD.Print($"[classic] {level}: terrain on the title's landscape shaders, {patches.Count} patch(es)");
                        }
                        else
                        {
                            if (Refii.Models.Engine_ClassicMaterial.Active)
                            {
                                GD.Print($"[classic] {level}: terrain stays on the modern splat ({classicError})");
                            }
                            chunks = Engine_LevelMesh.ToChunkMeshes(Engine_GhfTerrain.BuildMesh(heights), name);
                        }
                    }
                    else if (main && terrainRead is null &&
                        ResourceLoader.Load<ArrayMesh>(meshPath, "ArrayMesh") is { } whole)
                    {
                        chunks.Add(whole);
                    }
                    else
                    {
                        GD.Print($"[level] {level}: heightfield {landscape.Ghf} did not decode ({ghfError})");
                    }

                    if (!main)
                    {
                        GD.Print(
                            $"[level] {level}: landscape {landscape.Ehf} drawn from its grid " +
                            $"{landscape.Ghf} ({chunks.Count} chunks, " +
                            (material is null ? "NO splat material" : "splat material") + ")");
                    }

                    built.Add(new BuiltLandscape(landscape, name, true, chunks, material));
                    continue;
                }

                if (terrainRead?.Invoke(landscape.Ehf) is not { } ehfBytes)
                {
                    GD.Print($"[level] {level}: landscape {landscape.Ehf} is not in any bank");
                    continue;
                }

                string vistaName = $"{AdjacentNodeName}{vistas++}";

                // The vista's own height grid — the texture the guest's vertex shader samples —
                // through the same mesh and chunking as a stated .ghf.
                string pageError = "height grid did not decode";
                if (Engine_EhfHeights.TryDecode(ehfBytes, out GhfHeights vistaHeights, out string heightError) &&
                    Engine_EhfPages.TryBuild(ehfBytes, vistaHeights, out Engine_EhfPages.Built paged, out pageError))
                {
                    interactionGround.Add(vistaHeights);
                    // The guest's own background draw: chunks textured from the colour pages.
                    GD.Print(
                        $"[vista] {level}: {vistaName} from {landscape.Ehf} " +
                        $"({vistaHeights.Width}x{vistaHeights.Height} at {vistaHeights.Spacing} m, " +
                        $"{paged.Chunks} chunks on {paged.Patches} colour page(s))");
                    built.Add(new BuiltLandscape(
                        landscape, vistaName, false, paged.Meshes, null, Paged: true));
                    continue;
                }

                if (heightError.Length == 0)
                {
                    GD.Print($"[vista] {level}: {vistaName} colour pages not built ({pageError}) — falling back");
                }

                material = LoadTerrainMaterial(landscape.Ehf, source, findByName, vista: true);

                if (Engine_EhfHeights.TryDecode(ehfBytes, out vistaHeights, out heightError))
                {
                    interactionGround.Add(vistaHeights);
                    TerrainMesh gridMesh = Engine_GhfTerrain.BuildMesh(vistaHeights);
                    GD.Print(
                        $"[vista] {level}: {vistaName} from {landscape.Ehf} " +
                        $"({vistaHeights.Width}x{vistaHeights.Height} at {vistaHeights.Spacing} m, " +
                        $"{gridMesh.TriangleCount} tris, " +
                        (material is null ? "NO splat material" : material.GetClass()) + ")");
                    built.Add(new BuiltLandscape(
                        landscape, vistaName, false, Engine_LevelMesh.ToChunkMeshes(gridMesh, vistaName), material));
                    continue;
                }

                if (BuildPatches(level, landscape.Ehf, terrainRead) is not { } patchMesh)
                {
                    continue;
                }

                GD.Print(
                    $"[vista] {level}: {vistaName} from {landscape.Ehf} PATCHES ONLY — height grid " +
                    $"did not decode ({heightError}); {patchMesh.TriangleCount} tris, " +
                    (material is null ? "NO splat material" : material.GetClass()));

                // ONE MESH, never ToChunkMeshes: the fused patches carry no grid (Width/Height 0),
                // and chunking a gridless mesh yields nothing.
                built.Add(new BuiltLandscape(
                    landscape, vistaName, false,
                    new List<ArrayMesh> { Engine_LevelMesh.ToArrayMesh(patchMesh, vistaName) }, material));
            }

            return built;
        }

        var terrainTask =
            OS.GetThreadCallerId() == OS.GetMainThreadId()
                ? null
                : System.Threading.Tasks.Task.Run(TerrainWork);

        var root = new Node3D
        {
            Name = level.SceneNodeName,
            Scale = new Vector3(-1f, 1f, 1f),
        };
        root.SetMeta(Refii.Save.Engine_GoldenCockPlacements.WorldMeta, level.World);
        root.SetMeta(Refii.Save.Engine_GoldenCockPlacements.PathMeta, level.LevelPath);
        if (Refii.Models.Engine_ClassicMaterial.Active)
        {
            // The classic look's editor drive; inert in the game, which pumps the path itself.
            var driver = new Refii.Models.Engine_ClassicEditorDriver { Name = Refii.Models.Engine_ClassicEditorDriver.NodeName };
            root.AddChild(driver);
            driver.Owner = root;
        }

        AddWater(level, stated, source, findByName, root);
        Mark("water");

        bool hasMist = AddMist(level, stated, source, root);
        Mark("mist");

        LightmapContainer? bakedLightmaps = null;
        LightProbeField? probeField = null;
        string bakedGdbPath = string.Empty;
        if (source is not null)
        {
            Engine_LevelGdb.TryRead(level, p => source(p), out bakedGdbPath);
            GD.Print($"[level] {level}: scenario='{level.Scenario}' gdb={bakedGdbPath}");
            Engine_LevelBakedLighting.TryLoad(
                bakedGdbPath, p => source(p), out bakedLightmaps, out probeField);
            Engine_LevelBakedLighting.CurrentProbes = probeField;

            GD.Print(
                $"[baked] {level}: {Engine_LevelBakedLighting.Describe(bakedLightmaps, probeField)}");
            Engine_LevelShadowMaps.LoadOrClear(level, bakedGdbPath);
        }
        else
        {
            Engine_LevelShadowMaps.LoadOrClear(level, string.Empty);
        }

        Mark("baked-lighting");

        LightingFrame? lightingFrame = null;
        SkyTheme? resolvedSkyTheme = null;
        SkyFrame? resolvedSkyFrame = null;
        if (source is not null)
        {
            List<byte[]> lightingGdbs =
                Engine_SkyEnvironment.BuildGdbChain(level, p => source(p));
            if (lightingGdbs.Count != 0 &&
                Engine_EnvironmentTheme.TryExtractSky(lightingGdbs, out SkyTheme lightingSky))
            {
                float lightingTod =
                    lightingSky.SourceTimeOfDay >= 0f ? lightingSky.SourceTimeOfDay : 0.5f;
                string? forcedLightingTod =
                    System.Environment.GetEnvironmentVariable("REFII_FORCE_TOD");
                if (forcedLightingTod is not null &&
                    float.TryParse(
                        forcedLightingTod, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float forcedLighting))
                {
                    lightingTod = forcedLighting;
                }

                if (Engine_ThemeTimeline.TryExtractSkyBlended(
                        lightingGdbs, lightingTod, out SkyTheme blendedSky))
                {
                    lightingSky = blendedSky;
                }

                SkyFrame lightingSkyFrame = Engine_SkyFrame.Evaluate(lightingSky, lightingTod);
                resolvedSkyTheme = lightingSky;
                resolvedSkyFrame = lightingSkyFrame;

                if (Engine_ThemeTimeline.TryExtractLightingBlended(
                        lightingGdbs, lightingTod, out LightingTheme lightingTheme))
                {
                    lightingFrame = Engine_LightingFrame.Evaluate(
                        lightingSky, lightingTheme, lightingSkyFrame);
                }

                if (Engine_ThemeTimeline.TryExtractWaterBlended(
                        lightingGdbs, lightingTod, out WaterTheme waterTheme))
                {
                    ApplyWaterTheme(root, waterTheme);
                }
            }
        }

        Mark("sky+lighting");

        Refii.Entities.EntityInspectData? inspect = null;
        if (source is not null)
        {
            inspect = Refii.Entities.Engine_EntityInspect.Build(level, p => source(p));
        }

        Mark("inspect");

        if (source is not null)
        {
            Node3D? props = Engine_LevelProps.Build(
                level, p => source(p), probeField, lightingFrame, bakedLightmaps, inspect,
                out PropBuildStats propStats);

            if (props is not null)
            {
                root.AddChild(props);
                SetOwnerRecursive(props, root);
            }

            GD.Print($"[props] {level}: {propStats}");
            Mark("props");

            Node3D? entities = Refii.Entities.Engine_LevelEntities.Build(
                level, p => source(p), probeField, lightingFrame, inspect,
                out Refii.Entities.EntityPlaceStats entityStats);

            if (entities is not null)
            {
                root.AddChild(entities);
                SetOwnerRecursive(entities, root);
            }

            GD.Print($"[entities] {level}: {entityStats}");
            Mark("entities");

            if (inspect is not null)
            {
                Node3D? markers = Refii.Entities.Engine_EntityInspect.BuildMarkers(inspect);
                if (markers is not null)
                {
                    root.AddChild(markers);
                    markers.Owner = root;
                    foreach (Node child in markers.GetChildren())
                    {
                        child.Owner = root;
                    }

                    GD.Print(
                        $"[inspect] {level}: {markers.GetChildCount()} " +
                        "container marker(s) without a placed prop");
                }
            }

            if (Engine.IsEditorHint() || ForceEditorMode)
            {
                var spawnedHashes = new HashSet<uint>();
                LevelRecordMetadata.CollectRecordHashes(props, spawnedHashes, includeCulled: true);
                LevelRecordMetadata.CollectRecordHashes(entities, spawnedHashes, includeCulled: true);

                Node3D? levelMarkers = Engine_LevelMarkers.Build(
                    level, p => source(p), spawnedHashes, out MarkerBuildStats markerStats);
                if (levelMarkers is not null)
                {
                    root.AddChild(levelMarkers);
                    SetOwnerRecursive(levelMarkers, root);
                    LevelRecordMetadata.CollectRecordHashes(levelMarkers, spawnedHashes, includeCulled: true);
                    GD.Print($"[markers] {level}: {markerStats}");
                }

                if (!string.IsNullOrEmpty(bakedGdbPath))
                {
                    MeshInstance3D? navGraph = Engine_KynapseGraph.Build(level, p => source(p), bakedGdbPath);
                    if (navGraph is not null)
                    {
                        root.AddChild(navGraph);
                        SetOwnerRecursive(navGraph, root);
                    }

                    root.SetMeta(LevelRecordMetadata.LevelGdbPathMeta, bakedGdbPath);
                    root.SetMeta(
                        LevelRecordMetadata.LevelSavePathMeta,
                        Engine_LevelGdb.SiblingWithExtension(bakedGdbPath, ".save"));
                }

                root.SetMeta(LevelRecordMetadata.LevelNameMeta, level.Name);
                root.SetMeta(LevelRecordMetadata.ScenarioNameMeta, level.Scenario);
                string[] scenarios = level.Scenarios.Length > 0
                    ? level.Scenarios
                    : !string.IsNullOrEmpty(level.Scenario)
                        ? new[] { level.Scenario }
                        : new[] { Engine_LevelManifest.DefaultScenario };
                root.SetMeta(LevelRecordMetadata.AvailableScenariosMeta, scenarios);

                var spawnedArray = new long[spawnedHashes.Count];
                int idx = 0;
                foreach (uint h in spawnedHashes)
                {
                    spawnedArray[idx++] = h;
                }

                root.SetMeta(LevelRecordMetadata.SpawnedRecordHashesMeta, spawnedArray);
                Mark("editor-overlays");
            }
        }

        if (source is not null)
        {
            WorldEnvironment? sky = Engine_SkyEnvironment.Build(
                level, p => source(p), out _, out WeatherTheme weather);
            if (sky is not null)
            {
                root.AddChild(sky);
                sky.Owner = root;
            }

            float windAzimuth = weather.HasWind
                ? (weather.WindXyRotationMin + weather.WindXyRotationMax) * 0.5f *
                  (MathF.PI / 180f)
                : 0f;
            float windStrength = weather.HasWind
                ? (weather.WindStrengthMin + weather.WindStrengthMax) * 0.5f
                : 0f;

            // THE SAME DIRECTION THE RAIN USES, so vegetation and weather cannot disagree about
            // which way it blows — but the DIRECTION ONLY, as a unit vector. windStrength is not
            // applied: its units were never established, and scaling by it bent every tree flat.
            //
            // Set BEFORE the props are built, because materials take it at construction — pushing
            // it afterwards would leave this level's plants on the previous level's wind.
            Refii.Models.Engine_MdlMaterial.WindDirection = weather.HasWind
                ? new Vector2(MathF.Cos(windAzimuth), MathF.Sin(windAzimuth))
                : Vector2.Zero;

            // The cloth takes the full vector: the guest's is direction × speed in its Z-up
            // world, so the azimuth lies in its XY plane and is turned into ours here.
            Engine_Weather.WindWorld = weather.HasWind
                ? Refii.Models.Engine_GmdSkeleton.ZUpToYUp *
                  (new Vector3(MathF.Cos(windAzimuth), MathF.Sin(windAzimuth), 0f) * windStrength)
                : Vector3.Zero;

            MeshInstance3D? precipitation =
                Engine_Weather.Build(weather, windAzimuth, windStrength);
            if (precipitation is not null)
            {
                root.AddChild(precipitation);
                precipitation.Owner = root;
                if (Engine_DynamicSky.Enabled && Engine_DynamicSky.HideGameWeather)
                {
                    precipitation.Visible = false;
                }
            }

            if (Engine_DynamicSky.Enabled &&
                Engine_DynamicPrecipitation.Build() is { } dynamicPrecipitation)
            {
                root.AddChild(dynamicPrecipitation);
                dynamicPrecipitation.Owner = root;
            }

            List<byte[]> gdbs = Engine_SkyEnvironment.BuildGdbChain(level, p => source(p));
            if (resolvedSkyTheme is not null && resolvedSkyFrame is not null)
            {
                SkyTheme skyTheme = resolvedSkyTheme;
                SkyFrame frame = resolvedSkyFrame;

                if (lightingFrame is not null)
                {
                    DirectionalLight3D sun = Engine_LevelLighting.BuildSun(lightingFrame);
                    root.AddChild(sun);
                    sun.Owner = root;

                    if (sky is not null)
                    {
                        Engine_LevelLighting.ConfigureEnvironment(sky.Environment, lightingFrame);

                        if (hasMist)
                        {
                            // THE MIST VOLUMES ARE THE ONLY FOG. Godot's volumetric fog has a
                            // level-wide density of its own (default 0.05) that fills every cubic
                            // metre once the effect is enabled, lit by every torch — which is
                            // what turned the guild hall into an orange soup. The authored mist
                            // is a set of placed FogVolumes; the global density is not authored
                            // and goes to zero.
                            sky.Environment.VolumetricFogEnabled = true;
                            sky.Environment.VolumetricFogDensity = 0f;
                        }
                    }

                    if (sky is Engine_SkyEnvironmentNode skyNode)
                    {
                        skyNode.LightingGdbs.AddRange(gdbs);
                        skyNode.Sun = sun;
                        skyNode.Painted = LoadPaintedDaySets(level, stated, source, gdbs);
                    }

                    // BOTH THE STATED FIELDS AND THE SOLVED CURVE. The two densities are control
                    // points rather than amounts, so the stated numbers on their own say nothing
                    // about how thick the fog will look — the exponent is what decides that, and
                    // it is the value to check first when a region reads as too clear or as
                    // pea soup.
                    AtmosphereInputs fogInputs =
                        Engine_SkyAtmosphere.FromFrame(frame, skyTheme);
                    var fogState = new AtmosphereState();
                    Engine_SkyAtmosphere.SolveFogCurve(fogInputs, fogState);

                    GD.Print(
                        $"[lighting] {level}: tod={frame.TimeOfDay:0.##} " +
                        $"sun=({lightingFrame.SunColour[0]:0.00}," +
                        $"{lightingFrame.SunColour[1]:0.00}," +
                        $"{lightingFrame.SunColour[2]:0.00})x{lightingFrame.SunEnergy:0.00} " +
                        $"shadow={lightingFrame.SunShadow} " +
                        $"ambient={(lightingFrame.HasAmbient ? lightingFrame.AmbientEnergy.ToString("0.00") : "probes")}");

                    GD.Print(
                        $"[fog] {level}: " +
                        (fogState.HasFog
                            ? $"start={fogInputs.FoggingStart:0.#} " +
                              $"near={fogInputs.NearDistance:0.#}@{fogInputs.NearDensity:0.###} " +
                              $"far={fogInputs.FarDistance:0.#}@{fogInputs.FarDensity:0.###} " +
                              $"-> depth={fogState.FogOpticalDepthAtFar:0.###} " +
                              $"range={1f / fogState.FogRecipFarRange:0.#} " +
                              $"k={fogState.FogExponent:0.###} "
                            : "off ") +
                        $"closergb=({skyTheme.FogColour[0]:0.000}," +
                        $"{skyTheme.FogColour[1]:0.000},{skyTheme.FogColour[2]:0.000}) " +
                        $"closemax={skyTheme.CloseFogMaxDistance:0.#}");
                }

                Node3D? celestial = Engine_CelestialElements.Build(
                    skyTheme, frame, p => source(p));

                if (celestial is not null)
                {
                    root.AddChild(celestial);
                    celestial.Owner = root;
                    foreach (Node child in celestial.GetChildren())
                    {
                        child.Owner = root;
                    }

                    if (Engine_DynamicSky.Enabled && Engine_DynamicSky.HideGameCelestial)
                    {
                        celestial.Visible = false;
                    }
                }

                if (Engine_ThemeTimeline.TryExtractCloudsBlended(
                        gdbs, frame.TimeOfDay, out CloudTheme cloudTheme))
                {
                    Node3D? clouds = Engine_Clouds.Build(
                        cloudTheme,
                        gdbs,
                        frame.MainLightDirection,
                        frame.MainLightColour,
                        p => source(p),
                        findByName is null ? null : n => findByName(n));

                    if (clouds is not null)
                    {
                        root.AddChild(clouds);
                        clouds.Owner = root;
                        foreach (Node child in clouds.GetChildren())
                        {
                            child.Owner = root;
                        }

                        if (Engine_DynamicSky.Enabled && Engine_DynamicSky.HideGameClouds)
                        {
                            clouds.Visible = false;
                        }
                    }
                }
            }
        }

        Mark("rest");

        List<BuiltLandscape> landscapes =
            terrainTask is not null ? terrainTask.GetAwaiter().GetResult() : TerrainWork();
        if (root.GetNodeOrNull<Engine_SkyEnvironmentNode>(Engine_SkyEnvironment.NodeName) is { } interactionSky)
            interactionSky.InteractionGround = interactionGround;

        if (landscapes.Count == 0 || !landscapes[0].Grid || landscapes[0].Chunks.Count == 0)
        {
            root.QueueFree();
            error = $"terrain mesh did not load from {meshPath}";
            return null;
        }

        // Chunk instances, each with its landscape's splat material: the chunks are what the
        // passes cull. Every full-grid landscape lands in the one `Terrain` container
        // (TerrainChunksOf), the scenario's own heightfield first; each patch-built landscape
        // has a container of its own.
        List<MeshInstance3D> AddChunks(Node3D container, string name, List<ArrayMesh> chunks, Material? material)
        {
            var nodes = new List<MeshInstance3D>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = new MeshInstance3D
                {
                    Layers = Refii.Level.Engine_ReflectionLayer.ReflectedLayers,
                    Name = $"{name}{i}",
                    Mesh = chunks[i],
                };
                if (material is not null)
                {
                    chunk.MaterialOverride = material;
                    if (chunks[i].GetSurfaceCount() > 0)
                    {
                        chunks[i].SurfaceSetMaterial(0, material);
                    }
                }

                container.AddChild(chunk);
                chunk.Owner = root;
                nodes.Add(chunk);
            }

            return nodes;
        }

        Node3D Container(string name)
        {
            var container = new Node3D { Name = name };
            root.AddChild(container);
            container.Owner = root;
            return container;
        }

        Node3D terrainContainer = Container(TerrainNodeName);
        var allTerrainNodes = new List<MeshInstance3D>();
        var surfaces = new List<(string Path, MeshInstance3D Node)>();
        foreach (BuiltLandscape built in landscapes)
        {
            if (built.Chunks.Count == 0)
            {
                continue;
            }

            // A vista with no colour pages and no material is not drawn: it would take Godot's
            // default white.
            if (!built.Grid && !built.Paged && built.Material is null)
            {
                GD.Print($"[vista] {level}: {built.Name} ({built.Landscape.Ehf}) NOT DRAWN — no splat and no placeable colour pages");
                continue;
            }

            List<MeshInstance3D> nodes = AddChunks(
                built.Grid ? terrainContainer : Container(built.Name), built.Name, built.Chunks, built.Material);
            allTerrainNodes.AddRange(nodes);
            if (built.Paged)
            {
                // Each patch carries its own page material, so each takes the bake; and a vista,
                // like every guest landscape, is never a sun caster.
                foreach (MeshInstance3D node in nodes)
                {
                    node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    surfaces.Add((built.Landscape.Ehf, node));
                    if (node.Mesh is { } pageMesh && pageMesh.GetSurfaceCount() > 0 &&
                        pageMesh.SurfaceGetMaterial(0) is ShaderMaterial pageMaterial &&
                        pageMaterial.HasMeta(Engine_ClassicVista.VistaMeta))
                    {
                        Engine_ClassicVistaMist.Track(node);
                    }
                }
            }
            else if (nodes[0].MaterialOverride is null)
            {
                // The classic look's patches each carry their own layer chain, so each takes the
                // bake (one lightmap per heightfield, decoded once).
                foreach (MeshInstance3D node in nodes)
                {
                    surfaces.Add((built.Landscape.Ehf, node));
                }
            }
            else
            {
                surfaces.Add((built.Landscape.Ehf, nodes[0]));
            }
        }

        if (bakedLightmaps is not null && source is not null)
        {
            // One node per terrain: the lightmap binds to the material every chunk shares.
            BindTerrainLightmaps(level, surfaces, bakedLightmaps, bakedGdbPath, source);
        }

        if (lightingFrame is not null)
        {
            foreach (MeshInstance3D chunk in allTerrainNodes)
            {
                Engine_LevelBakedLighting.ApplyInstanceLighting(chunk, lightingFrame);
            }
        }

        Mark("terrain-join");

        Refii.Save.Engine_GoldenCockPlacements.AddToLevel(root, level);

        GD.Print($"[level-build] {level}: {clock.ElapsedMilliseconds} ms of construction — " +
            string.Join("  ", phases.ConvertAll(p => $"{p.Name} {p.Ms}ms")));

        return root;
    }

    /// <summary>
    /// Gives the terrain the bake its own heightfield names, and says so for any it does not.
    /// </summary>
    /// <remarks>
    /// The heightfields are matched by PATH because that is what the type-4 record carries — a
    /// backslashed asset path and the eight bytes of its lightmap id. A level that ships bakes
    /// for heightfields we do not draw is a normal state, not an error: the sea vista is merged
    /// into one distant mesh with no splat material of its own, so its bake has nowhere to go
    /// yet. That is reported rather than dropped in silence, which is how the wrong bake went
    /// unnoticed on the ground in the first place.
    /// </remarks>
    private static void BindTerrainLightmaps(
        LevelEntry level,
        List<(string Path, MeshInstance3D Node)> surfaces,
        LightmapContainer lightmaps,
        string gdbPath,
        Func<string, byte[]?> source)
    {
        string enginePath = Engine_LevelGdb.EngineLevelPath(level, source, gdbPath);
        byte[]? bytes = source(enginePath) ?? source("data/" + enginePath);
        if (bytes is null)
        {
            GD.Print($"[baked] {level}: no .engine_level at {enginePath}, terrain unlit");
            return;
        }

        EngineLevelFile engineLevel = Engine_EngineLevelFile.Parse(bytes);
        var bound = new HashSet<ulong>();
        var decoded = new Dictionary<ulong, ImageTexture?>();
        var reported = new HashSet<ulong>();

        foreach ((string surfacePath, MeshInstance3D node) in surfaces)
        {
            // The splat override, or — for a page-built vista — every surface's own page material.
            var targets = new List<ShaderMaterial>();
            if (node.MaterialOverride is ShaderMaterial overridden)
            {
                targets.Add(overridden);
            }
            else if (node.Mesh is { } surfacesMesh)
            {
                for (int i = 0; i < surfacesMesh.GetSurfaceCount(); i++)
                {
                    if (surfacesMesh.SurfaceGetMaterial(i) is ShaderMaterial own)
                    {
                        targets.Add(own);
                    }
                }
            }

            if (targets.Count == 0)
            {
                GD.Print(
                    $"[baked] {level}: {surfacePath} carries " +
                    (node.MaterialOverride is null ? "no material" : node.MaterialOverride.GetClass()) +
                    ", not the splat — its bake has nowhere to go");
                continue;
            }

            ShaderMaterial splat = targets[0];

            foreach (EngineLevelHeightfield heightfield in engineLevel.Heightfields)
            {
                if (!SameAsset(heightfield.Path, surfacePath))
                {
                    continue;
                }

                if (!Engine_LevelBakedLighting.TryGetTerrainLightmap(
                        lightmaps, heightfield.LightmapKey, out TerrainLightmap bake))
                {
                    GD.Print(
                        $"[baked] {level}: heightfield states lightmap " +
                        $"0x{heightfield.LightmapKey:X16} but the .lmp has no such entry");
                    break;
                }

                if (!decoded.TryGetValue(heightfield.LightmapKey, out ImageTexture? texture))
                {
                    texture = Engine_LevelBakedLighting.DecodeTexture(bake.Blob);
                    decoded[heightfield.LightmapKey] = texture;
                }
                if (texture is null)
                {
                    GD.Print($"[baked] {level}: lightmap 0x{heightfield.LightmapKey:X16} did not decode");
                    break;
                }

                foreach (ShaderMaterial target in targets)
                {
                    target.SetShaderParameter("lightmap", texture);
                    target.SetShaderParameter("has_lightmap", true);
                }
                // A classic vista page takes the background's lightmapped variant, the bake on
                // sampler 3 (`sub_822AA878`) at (position - origin) * (1 / (W s), 1 / (H s)).
                if (node.MaterialOverride is null && node.Mesh is { } vistaMesh)
                {
                    for (int i = 0; i < vistaMesh.GetSurfaceCount(); i++)
                    {
                        if (vistaMesh.SurfaceGetMaterial(i) is ShaderMaterial page &&
                            page.HasMeta(Engine_ClassicVista.VistaMeta) && page.HasMeta(Engine_ClassicVista.GridMeta))
                        {
                            Vector3 grid = page.GetMeta(Engine_ClassicVista.GridMeta).AsVector3();
                            if (Engine_ClassicVista.Lightmapped(page, texture, new Vector2(grid.X, grid.Y),
                                    new Vector2(1f / (texture.GetWidth() * grid.Z), 1f / (texture.GetHeight() * grid.Z))) is { } litPage)
                            {
                                vistaMesh.SurfaceSetMaterial(i, litPage);
                            }
                        }
                    }
                }
                // The title's patches take the lightmapped (AMB1) pair with the map on sampler 3,
                // for EVERY layer: each layer pass carries its weighted share of the ambient,
                // which on the title's terrain is the bake.
                if (node.MaterialOverride is null && node.Mesh is { } patchMesh)
                {
                    for (int i = 0; i < patchMesh.GetSurfaceCount(); i++)
                    {
                        if (patchMesh.SurfaceGetMaterial(i) is not ShaderMaterial own ||
                            !own.HasMeta(Refii.Models.Engine_ClassicMaterial.ClassicMeta))
                        {
                            continue;
                        }
                        ShaderMaterial? head = null, tail = null;
                        for (Material? pass = own; pass is ShaderMaterial layer; pass = layer.NextPass)
                        {
                            ShaderMaterial lit = Refii.Models.Engine_ClassicMaterial.WithAmbient(layer, 1, texture, wait: true) ?? layer;
                            if (ReferenceEquals(lit, layer))
                            {
                                lit = Refii.Models.Engine_MatCopy.Copy(layer, "level-lit");
                            }
                            // g_LandscapeLightmapUVFactor (VS c14), per landscape (`sub_82A8EA50`):
                            // (1 / (W * s), 1 / (H * s), 1, 1), the bake's texel size over the
                            // heightfield's metres per cell.
                            float cell = layer.HasMeta(Engine_ClassicTerrain.CellSizeMeta)
                                ? layer.GetMeta(Engine_ClassicTerrain.CellSizeMeta).AsSingle()
                                : Engine_GhfTerrain.CellSize;
                            lit.SetShaderParameter("vs_g_LandscapeLightmapUVFactor", new Vector4(
                                1f / (texture.GetWidth() * cell), 1f / (texture.GetHeight() * cell), 1f, 1f));
                            lit.NextPass = null;
                            if (head is null)
                            {
                                head = lit;
                            }
                            else
                            {
                                tail!.NextPass = lit;
                            }
                            tail = lit;
                        }
                        if (head is not null)
                        {
                            patchMesh.SurfaceSetMaterial(i, head);
                        }
                    }
                }

                bound.Add(heightfield.LightmapKey);

                // One texel per CELL is what makes the plain normalised heightfield coordinate
                // the right UV, so say it out loud when it is not — a bake built for a
                // different lattice is a real failure mode and it looks like bad lighting
                // rather than like a mismatch.
                bool cellAligned =
                    texture.GetWidth() == bake.SamplesX - 1 &&
                    texture.GetHeight() == bake.SamplesY - 1;

                if (!reported.Add(heightfield.LightmapKey))
                {
                    break;
                }
                GD.Print(
                    $"[baked] {level}: terrain lightmap 0x{heightfield.LightmapKey:X16} " +
                    $"({texture.GetWidth()}x{texture.GetHeight()} over " +
                    $"{bake.SamplesX}x{bake.SamplesY} samples" +
                    (cellAligned ? "" : ", NOT CELL-ALIGNED") +
                    $") applied from {heightfield.Path}");
                break;
            }
        }

        foreach (EngineLevelHeightfield heightfield in engineLevel.Heightfields)
        {
            if (!bound.Contains(heightfield.LightmapKey) &&
                lightmaps.TerrainLightmapBlobs.ContainsKey(heightfield.LightmapKey))
            {
                GD.Print(
                    $"[baked] {level}: lightmap 0x{heightfield.LightmapKey:X16} for " +
                    $"{heightfield.Path} is shipped but nothing draws that heightfield");
            }
        }
    }

    /// <summary>
    /// Whether a type-4 record's asset path and one of ours name the same file.
    /// </summary>
    /// <remarks>
    /// The .engine_level writes Windows paths — <c>worlds\albion\...\x.ehf</c> — while ours are
    /// the VFS's forward-slashed ones, and either may carry a directory prefix the other does
    /// not. Comparing the file name alone is enough to be unambiguous inside one level and is
    /// the only part both spellings agree on.
    /// </remarks>
    private static bool SameAsset(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return string.Equals(LeafName(a), LeafName(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string LeafName(string path)
    {
        int cut = path.LastIndexOfAny(new[] { '/', '\\' });
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// A landscape's material: its splat, else its pre-baked colour pages. A VISTA takes the pages
    /// first. The guest draws a patch past 64 m through the background path (`sub_82236508`), which
    /// samples those pages, not the material palette — and a vista is always that far, so its
    /// palette is an editor default nobody sees (`art\editor\invalid_texture.tex` on a market
    /// vista, painted magenta wherever no layer covers).
    /// </summary>
    private static Material? LoadTerrainMaterial(
        string ehfPath,
        Func<string, byte[]?>? source = null,
        Func<string, string?>? findByName = null,
        bool vista = false)
    {
        if (vista && LoadPageMaterial(ehfPath) is { } pages)
        {
            return pages;
        }

        if (source?.Invoke(ehfPath) is { } ehfBytes &&
            Engine_TerrainSplatMaterial.TryBuild(
                ehfBytes,
                p => source(p),
                findByName is null ? null : n => findByName(n),
                out ShaderMaterial builtSplat,
                out _))
        {
            builtSplat.ResourceName = ehfPath[(ehfPath.LastIndexOf('/') + 1)..];
            return builtSplat;
        }

        string materialPath =
            Engine_BnkTerrainMaterialLoader.PathPrefix + ehfPath +
            Engine_BnkTerrainMaterialLoader.PathSuffix;

        var splat = ResourceLoader.Load<ShaderMaterial>(materialPath, "ShaderMaterial");
        if (splat is not null)
        {
            return splat;
        }

        if (LoadPageMaterial(ehfPath) is { } fallbackPage)
        {
            return fallbackPage;
        }

        if (GD.Load<Shader>(Engine_TerrainSplatMaterial.ShaderPath) is { } shader)
        {
            return new ShaderMaterial { Shader = shader };
        }

        return new StandardMaterial3D
        {
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Roughness = 0.9f,
            Metallic = 0f,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        };
    }

    /// <summary>The landscape's pre-baked colour pages as one albedo.</summary>
    private static Material? LoadPageMaterial(string ehfPath)
    {
        string albedoPath = Engine_BnkTerrainAlbedoLoader.PathPrefix + ehfPath;
        var albedo = ResourceLoader.Load<Texture2D>(albedoPath, "Texture2D");
        if (albedo is null)
        {
            return null;
        }

        return new StandardMaterial3D
        {
            AlbedoTexture = albedo,
            Metallic = 0f,
            Roughness = 0.9f,

            // No built-in highlight: a placed light's specular scalar is its light-table slot
            // (Engine_LightTable), not a strength.
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    private static void Preload(LevelEntry level, ScenarioManifest manifest, LevelReads reads)
    {
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string path)
        {
            if (path.Length != 0 && seen.Add(path))
            {
                wanted.Add(path);
            }
        }

        void AddBucket(List<string> bucket)
        {
            foreach (string line in bucket)
            {
                Add(Engine_LevelManifest.VirtualPath(line));
            }
        }

        AddBucket(manifest.Gdb);
        AddBucket(manifest.EngineTheme);
        AddBucket(manifest.Ghf);
        AddBucket(manifest.AudioMaps);
        AddBucket(manifest.Hdb);
        AddBucket(manifest.Genv);
        AddBucket(manifest.EngineLevel);
        AddBucket(manifest.AiConfig);
        AddBucket(manifest.Save);
        AddBucket(manifest.HavokScenario);
        AddBucket(manifest.Unrecognized);

        if (manifest.Gdb.Count != 0)
        {
            string stem = Engine_LevelManifest.VirtualPath(manifest.Gdb[0]);
            foreach (string extension in new[] { ".lmp", ".gdb", ".save", ".engine_level" })
            {
                Add(Engine_LevelGdb.SiblingWithExtension(stem, extension));
            }
        }

        foreach (string candidate in Engine_LevelVfsConfig.CandidatePaths(level))
        {
            Add(candidate);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int fetched, List<string> missing) = reads.Prefetch(wanted);

        string summary =
            $"[level-preload] {level}: {fetched}/{wanted.Count} manifest file(s) in " +
            $"{clock.ElapsedMilliseconds} ms";

        if (missing.Count != 0)
        {
            missing.Sort(StringComparer.Ordinal);
            var shown = new List<string>();
            for (int i = 0; i < missing.Count && shown.Count < 8; i++)
            {
                shown.Add(LeafName(missing[i]));
            }

            summary += $" — {missing.Count} unresolved: {string.Join(", ", shown)}" +
                       (missing.Count > shown.Count ? ", ..." : string.Empty);
        }

        GD.Print(summary);
    }

    private static void ReadGameOrderResources(
        LevelEntry level, ScenarioManifest manifest, Func<string, byte[]?> source)
    {
        var order = new (string Label, List<string> Bucket)[]
        {
            (".ghf  (terrain grid)", manifest.Ghf),
            ("audio map layer", manifest.AudioMaps),
            (".hdb  (height database)", manifest.Hdb),
            (".genv (env grid)", manifest.Genv),
        };

        foreach ((string label, List<string> bucket) in order)
        {
            foreach (string line in bucket)
            {
                string path = Engine_LevelManifest.VirtualPath(line);
                byte[]? bytes = source(path);
                GD.Print(bytes is not null
                    ? $"[level] {label} loaded ({bytes.Length} bytes)  {path}"
                    : $"[level] {label} load FAILED: {path}");
            }
        }
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        node.Owner = root;
        foreach (Node child in node.GetChildren())
        {
            SetOwnerRecursive(child, root);
        }
    }

    /// <summary>
    /// Every .water the .engine_level states (type 5, `sub_82AB9818` per record), into the one
    /// `Water` container. A body samples the grid of the landscape that shares its file's stem.
    /// </summary>
    private static void AddWater(
        LevelEntry level,
        StatedWorld stated,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        Node3D root)
    {
        if (source is null)
        {
            return;
        }

        var container = new Node3D { Name = WaterNodeName };
        int emitted = 0;
        int tiles = 0;
        int buried = 0;
        List<(StatedLandscape Landscape, GhfHeights? Grid)> grids = DecodeGrids(stated, source);
        Engine_LandscapeGround ground = GroundOf(grids);
        WaterBody? oceanBody = null;
        string oceanFile = string.Empty;
        Aabb? waterBounds = null;

        foreach (string waterPath in stated.Water)
        {
            byte[]? bytes = source(waterPath);
            if (bytes is null || bytes.Length <= 8)
            {
                continue;
            }

            if (!Engine_WaterFile.TryParse(bytes, out WaterScene scene, out string why))
            {
                GD.Print($"[water] {level}: {waterPath}: {why}");
                continue;
            }

            string ghfPath = stated.GridFor(waterPath);
            ImageTexture? terrainTexture = null;
            var terrainBounds = Vector4.Zero;
            GhfHeights? owned = ghfPath.Length != 0
                ? grids.Find(g => g.Landscape.Ghf == ghfPath).Grid
                : null;
            if (!Engine_WaterMaterial.GuestMode && owned is { Width: > 1, Height: > 1 } heights)
            {
                var data = new byte[heights.Values.Length * sizeof(float)];
                Buffer.BlockCopy(heights.Values, 0, data, 0, data.Length);
                Image image = Image.CreateFromData(
                    heights.Width, heights.Height, false, Image.Format.Rf, data);
                if (!image.IsEmpty())
                {
                    terrainTexture = ImageTexture.CreateFromImage(image);
                }
                terrainBounds = new Vector4(
                    heights.OriginX,
                    heights.OriginZ,
                    1f / ((heights.Width - 1) * heights.Spacing),
                    1f / ((heights.Height - 1) * heights.Spacing));
            }

            int fromFile = 0;
            float lowest = float.PositiveInfinity;
            for (int i = 0; i < scene.Bodies.Count; i++)
            {
                WaterBody body = scene.Bodies[i];

                // The first file with bodies states the ocean, at the LOWEST of ALL its bodies
                // (the guest's min over every body's box) — buried ones included.
                if ((oceanFile.Length == 0 || oceanFile == waterPath) && float.IsFinite(body.BaseHeight) &&
                    (oceanBody is null || body.BaseHeight < oceanBody.BaseHeight))
                {
                    oceanBody = body;
                    oceanFile = waterPath;
                }

                // UNDER THE GROUND IS NEVER SEEN: a heightfield is single-valued and opaque, so
                // a body none of whose water cells clears the ground under it draws nothing and
                // would still cost a mirror render whenever its box is in view.
                if (ground.GridCount != 0 && ground.OpenCells(body, true, out _) == 0)
                {
                    buried++;
                    continue;
                }

                TerrainMesh? mesh = Engine_WaterMesh.BuildBody(body);
                if (mesh is null)
                {
                    continue;
                }

                string name = $"WaterBody{emitted}";

                // The classic renderer draws the body with the title's own pair; the mesh's
                // texture coordinates become the body mask's before the mesh is built.
                string classicError = string.Empty;
                ShaderMaterial? classic = Engine_ClassicWater.Available
                    ? Engine_ClassicWater.Build(
                        body, mesh, p => source(p), findByName is null ? null : n => findByName(n), out classicError)
                    : null;
                if (Engine_ClassicWater.Available && classic is null)
                {
                    GD.Print($"[water] {level}: {classicError}; the modern water shader stands in");
                }

                var node = new MeshInstance3D
                {
                    Name = name,
                    Mesh = Engine_LevelMesh.ToArrayMesh(mesh, name),
                };

                string materialError = string.Empty;
                ShaderMaterial? material = classic ?? Engine_WaterMaterial.Build(
                    body,
                    p => source(p),
                    findByName is null ? null : n => findByName(n),
                    out materialError);

                if (material is null)
                {
                    GD.Print($"[water] {level}: {materialError}");
                }
                else if (classic is not null)
                {
                    node.MaterialOverride = classic;
                    Engine_ClassicWater.Attach(classic, node);
                }
                else
                {
                    if (terrainTexture is not null)
                    {
                        material.SetShaderParameter("terrain_height_tex", terrainTexture);
                        material.SetShaderParameter("terrain_bounds", terrainBounds);
                        material.SetShaderParameter("has_terrain_height", true);
                    }

                    Aabb bodyAabb = node.Mesh.GetAabb();
                    material.SetShaderParameter("w_body_bounds", new Vector4(
                        bodyAabb.Position.X,
                        bodyAabb.Position.Z,
                        bodyAabb.Position.X + bodyAabb.Size.X,
                        bodyAabb.Position.Z + bodyAabb.Size.Z));
                    material.SetShaderParameter("has_body_bounds", true);

                    node.MaterialOverride = material;
                }

                container.AddChild(node);
                emitted++;
                fromFile++;
                lowest = MathF.Min(lowest, body.BaseHeight);
                waterBounds = waterBounds is { } so_far ? so_far.Merge(node.Mesh.GetAabb()) : node.Mesh.GetAabb();

            }

            tiles += (int)scene.TileCount;
            GD.Print($"[water] {level}: {waterPath}: {fromFile} body(s), {scene.TileCount} tiles, lowest {lowest:0.###}" +
                (terrainTexture is null ? "" : $", ground from {ghfPath}"));
        }

        if (emitted == 0)
        {
            container.QueueFree();
            return;
        }

        if (oceanBody is not null && waterBounds is { } bounds && AllowsVistaOcean(level))
        {
            AddVistaOcean(level, container, oceanBody, oceanFile, bounds, source, findByName);
        }
        else if (oceanBody is not null && !AllowsVistaOcean(level))
        {
            GD.Print($"[water] {level}: inland level ({level.LevelPath}) — vista ocean omitted");
        }

        root.AddChild(container);
        container.Owner = root;

        foreach (Node child in container.GetChildren())
        {
            child.Owner = root;
        }

        GD.Print(
            $"[water] {level}: {emitted} body(s), {tiles} tiles from {stated.Water.Count} stated file(s)" +
            (buried != 0 ? $"; {buried} body(s) entirely under the ground not built" : string.Empty));
    }

    /// <summary>
    /// Whether a level features an open coastal ocean horizon that should receive the vista ocean sheet.
    /// Inland cities (e.g. Bowerstone Market/Slums), inland landscapes (Brightwood, Bower Lake, Fairfax,
    /// Oakfield), and enclosed dungeons/caves do not feature an ocean horizon and must not receive the 5.7 km sheet,
    /// which would otherwise slice through topography and flood low-lying streets and valleys.
    /// </summary>
    public static bool AllowsVistaOcean(LevelEntry level)
    {
        string path = (level.LevelPath ?? string.Empty).ToLowerInvariant().Replace('\\', '/');
        string name = (level.Name ?? string.Empty).ToLowerInvariant();
        string group = (level.Group ?? string.Empty).ToLowerInvariant();

        // Strip any leading world prefix ("albion/", etc.) for clean prefix matching
        string localPath = path.StartsWith("albion/") ? path["albion/".Length..] : path;

        // Enclosed caves, tombs, and demon doors never feature an open ocean horizon,
        // with the exception of coastal exterior transitional paths.
        bool isEnclosed = localPath.StartsWith("caves/") || localPath.StartsWith("tombs/") || localPath.StartsWith("demondoors/") ||
                          path.Contains("/caves/") || path.Contains("/tombs/") || path.Contains("/demondoors/");
        if (isEnclosed && !path.Contains("westcliffexterior") && !path.Contains("bloodstone_assault"))
        {
            return false;
        }

        // Inland landlocked cities, estates, and regions
        if (path.Contains("bwsmarket") || path.Contains("bwsslums") || path.Contains("bwscemetary") ||
            path.Contains("bowerstone") || path.Contains("cemetery") ||
            path.Contains("fairfax") || path.Contains("brightwood") || path.Contains("bowerlake") ||
            path.Contains("oakfield") || path.Contains("crucible") || path.Contains("dreamworld") ||
            path.Contains("chamberofseasons") || path.Contains("shrine") || path.Contains("dlc2") ||
            name.Contains("bwsmarket") || name.Contains("bwsslums") || name.Contains("bowerstone") ||
            name.Contains("fairfax") || name.Contains("brightwood") || name.Contains("bowerlake") ||
            group.Contains("bwsmarket") || group.Contains("bwsslums") || group.Contains("fairfax"))
        {
            return false;
        }

        // Coastal maritime regions featuring an open ocean horizon
        return path.Contains("bloodstone") ||
               path.Contains("bloodtsone") ||
               path.Contains("reaver beach") ||
               path.Contains("ravenscar") ||
               path.Contains("spire") ||
               path.Contains("mysteryisland") ||
               path.Contains("dunecrest") ||
               path.Contains("westcliff") ||
               path.Contains("wraithmarsh") ||
               path.Contains("rookridge") ||
               name.Contains("bloodstone") ||
               name.Contains("bloodtsone") ||
               name.Contains("reaver beach") ||
               name.Contains("ravenscar") ||
               name.Contains("spire") ||
               name.Contains("westcliff") ||
               name.Contains("wraithmarsh");
    }

    /// <summary>Each stated landscape with its height grid: the stated .ghf, else the .ehf's own.</summary>
    private static List<(StatedLandscape Landscape, GhfHeights? Grid)> DecodeGrids(
        StatedWorld stated, Func<string, byte[]?> source)
    {
        var grids = new List<(StatedLandscape, GhfHeights?)>();
        foreach (StatedLandscape landscape in stated.Landscapes)
        {
            GhfHeights? grid = null;
            if (landscape.Ghf.Length != 0)
            {
                if (source(landscape.Ghf) is { } ghf && Engine_GhfTerrain.TryDecode(ghf, out GhfHeights g, out _))
                {
                    grid = g;
                }
            }
            else if (landscape.Ehf.Length != 0 && source(landscape.Ehf) is { } ehf &&
                     Engine_EhfHeights.TryDecode(ehf, out GhfHeights e, out _))
            {
                grid = e;
            }

            grids.Add((landscape, grid));
        }

        return grids;
    }

    private static Engine_LandscapeGround GroundOf(List<(StatedLandscape Landscape, GhfHeights? Grid)> grids)
    {
        var ground = new Engine_LandscapeGround();
        foreach ((_, GhfHeights? grid) in grids)
        {
            if (grid is not null)
            {
                ground.Add(grid);
            }
        }

        return ground;
    }

    public const string VistaOceanNodeName = "VistaOcean";

    /// <summary>Half the ocean's side: the `sky_oceanwater` VS (blob 0129) spans ±2850 units.</summary>
    private const float VistaOceanHalfExtent = 2850f;

    /// <summary>
    /// The vista ocean — the unmasked sheet that carries a level's water to the horizon.
    /// </summary>
    /// <remarks>
    /// `sub_82A931E0` makes it after a .water file's bodies, from the minimum of their AABBs
    /// (lane 2 = height, 0x82a93370): desc `off_820F7EB4` → `sub_82B2EC08`, primitive 47
    /// (PRIMITIVE_INSTANCE_OCEANWATER), height - 0.1 (0x82b2eccc). Its draw (`sub_821D5068`)
    /// submits only while the theme's DisableVistaWater reads 0 and at most once per frame, so
    /// one is built: the first stated file's. Terrain hides it by depth; it samples no mask.
    /// Shaded with that file's lowest body; centred on the level's water.
    /// </remarks>
    private static void AddVistaOcean(
        LevelEntry level,
        Node3D container,
        WaterBody body,
        string waterPath,
        Aabb bounds,
        Func<string, byte[]?> source,
        Func<string, string?>? findByName)
    {
        if (Engine_ClassicWater.VistaAvailable)
        {
            float guestHeight = body.BaseHeight - 0.1f;
            Texture2D? normal = Engine_WaterMaterial.LoadTexture(
                body.NormalMapPath, p => source(p), findByName is null ? null : n => findByName(n));
            MeshInstance3D? classic = Engine_ClassicWater.BuildVista(guestHeight, normal, out string classicError);
            if (classic is not null)
            {
                classic.Name = VistaOceanNodeName;
                container.AddChild(classic);
                GD.Print($"[water] {level}: vista ocean on SKY_OCEANWATER at {guestHeight:0.###} from {waterPath}");
                return;
            }
            GD.Print($"[water] {level}: classic vista ocean failed ({classicError}); the modern sheet stands in");
        }

        ShaderMaterial? material = Engine_WaterMaterial.Build(
            body,
            p => source(p),
            findByName is null ? null : n => findByName(n),
            out string materialError);
        if (material is null)
        {
            GD.Print($"[water] {level}: vista ocean has no material ({materialError})");
            return;
        }

        material.SetShaderParameter("has_water_mask", false);
        material.SetShaderParameter("has_body_bounds", false);
        material.SetShaderParameter("has_terrain_height", false);
        material.SetShaderParameter("ocean_mode", true);

        float height = body.BaseHeight - 0.1f;
        Vector3 centre = bounds.GetCenter();
        float side = VistaOceanHalfExtent * 2f;
        var sheet = new GhfHeights
        {
            Width = 2,
            Height = 2,
            Values = new[] { height, height, height, height },
            MinHeight = height,
            MaxHeight = height,
            OriginX = centre.X - VistaOceanHalfExtent,
            OriginZ = centre.Z - VistaOceanHalfExtent,
            Spacing = side,
        };

        var node = new MeshInstance3D
        {
            Name = VistaOceanNodeName,
            Mesh = Engine_LevelMesh.ToArrayMesh(Engine_GhfTerrain.BuildMesh(sheet), VistaOceanNodeName),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        container.AddChild(node);
        GD.Print($"[water] {level}: vista ocean at {height:0.###} from {waterPath}, ±{VistaOceanHalfExtent} around ({centre.X:0.#}, {centre.Z:0.#})");
    }

    /// <summary>The theme's DisableVistaWater: the ocean shows only while it reads exactly 0.</summary>
    public static void ApplyVistaOceanGate(Node3D water, WaterTheme theme)
    {
        if (water.GetNodeOrNull<MeshInstance3D>(VistaOceanNodeName) is { } ocean)
        {
            ocean.Visible = theme[WaterThemeParam.DisableVistaWater] == 0f;
        }
    }

    private static void ApplyWaterTheme(Node3D root, WaterTheme theme)
    {
        Node3D? water = root.GetNodeOrNull<Node3D>(WaterNodePath);
        if (water is null)
        {
            return;
        }

        // By index: GetChildren() is a Godot Array, a finalizable handle per atmosphere tick.
        for (int i = 0, count = water.GetChildCount(); i < count; i++)
        {
            if (water.GetChild(i) is MeshInstance3D mesh &&
                mesh.MaterialOverride is ShaderMaterial material)
            {
                if (material.HasMeta(Engine_ClassicWater.Meta))
                {
                    Engine_ClassicWater.Apply(material, theme);
                }
                else
                {
                    Engine_WaterMaterial.Apply(material, theme);
                }
            }
        }

        ApplyVistaOceanGate(water, theme);
    }

    /// <summary>
    /// Every .mist the .engine_level states (type 32, `sub_82AB99F8` per record), into one
    /// `Mist` container. A file whose count is zero states no volume.
    /// </summary>
    private static bool AddMist(
        LevelEntry level,
        StatedWorld stated,
        Func<string, byte[]?>? source,
        Node3D root)
    {
        // Under classic the mist is the title's own (Engine_ClassicMist, the guest's volumes on
        // its shaders), and every title shader is fog_disabled: Godot's volumes would only cost.
        if (source is null || Refii.Models.Engine_ClassicMaterial.Active)
        {
            return false;
        }

        var container = new Node3D { Name = "Mist" };
        int emitted = 0;
        int records = 0;

        foreach (string mistPath in stated.Mist)
        {
            byte[]? bytes = source(mistPath);
            if (bytes is null || bytes.Length <= 8)
            {
                continue;
            }

            if (!Engine_MistFile.TryParse(bytes, out MistScene scene, out string why))
            {
                GD.Print($"[mist] {level}: {mistPath}: {why}");
                continue;
            }

            records += scene.Volumes.Count;
            for (int i = 0; i < scene.Volumes.Count; i++)
            {
                MistVolume vol = scene.Volumes[i];

                float sizeX = MathF.Abs(vol.BoxSize[0]);
                float sizeY = MathF.Abs(vol.BoxSize[1]);
                float sizeZ = MathF.Abs(vol.BoxSize[2]);
                if (sizeX < 0.01f && sizeY < 0.01f && sizeZ < 0.01f)
                {
                    continue;
                }

                float centreX = vol.BoxMin[0] + vol.BoxSize[0] * 0.5f;
                float centreY = vol.BoxMin[1] + vol.BoxSize[1] * 0.5f;
                float centreZ = vol.BoxMin[2] + vol.BoxSize[2] * 0.5f;

                var material = new FogMaterial();
                material.Density = vol.Density;
                var linearCol = new Color(vol.Colour[0], vol.Colour[1], vol.Colour[2]);
                material.Albedo = linearCol.LinearToSrgb();
                material.Emission = (linearCol * 0.25f).LinearToSrgb();
                if (vol.Falloff > 0f)
                {
                    material.HeightFalloff = vol.Falloff;
                }

                var fog = new FogVolume
                {
                    Name = $"MistVolume{emitted}",
                    Position = new Vector3(centreX, centreY, centreZ),
                    Size = new Vector3(sizeX, sizeY, sizeZ),
                    Shape = RenderingServer.FogVolumeShape.Box,
                    Material = material,
                };

                container.AddChild(fog);
                emitted++;
                GD.Print($"[mist] volume {i}: min=({vol.BoxMin[0]:0.#},{vol.BoxMin[1]:0.#},{vol.BoxMin[2]:0.#}) " +
                    $"size=({vol.BoxSize[0]:0.#},{vol.BoxSize[1]:0.#},{vol.BoxSize[2]:0.#}) density={vol.Density:0.####} " +
                    $"colour=({vol.Colour[0]:0.00},{vol.Colour[1]:0.00},{vol.Colour[2]:0.00}) falloff={vol.Falloff:0.###} recip={vol.Reciprocal:0.###}");
            }
        }

        if (emitted == 0)
        {
            container.QueueFree();
            return false;
        }

        root.AddChild(container);
        container.Owner = root;
        BakedMist = container;

        foreach (Node child in container.GetChildren())
        {
            child.Owner = root;
        }

        GD.Print(
            $"[mist] {level}: {emitted} volume(s) from {records} record(s) in {stated.Mist.Count} stated file(s)");

        return true;
    }

    /// <summary>The level's .mist volumes as baked at load; Engine_ModernMist hides them while the guest states its own.</summary>
    public static Node3D? BakedMist { get; private set; }

    private static string ReplaceExtension(string path, string extension)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path + extension : path[..dot] + extension;
    }

    private sealed record StatedLandscape(string Ehf, string Ghf);

    private sealed record BuiltLandscape(
        StatedLandscape Landscape, string Name, bool Grid, List<ArrayMesh> Chunks, Material? Material,
        bool Paged = false);

    /// <summary>What the scenario's .engine_level states, as VFS paths.</summary>
    private sealed class StatedWorld
    {
        /// <summary>The scenario's own heightfield first.</summary>
        public List<StatedLandscape> Landscapes { get; } = new();

        public List<string> Water { get; } = new();

        public List<string> Mist { get; } = new();

        /// <summary>The .ghf of the landscape whose stem a sibling file shares, or empty.</summary>
        public string GridFor(string sibling)
        {
            string stem = StemOf(sibling);
            foreach (StatedLandscape landscape in Landscapes)
            {
                if (landscape.Ghf.Length != 0 &&
                    StemOf(landscape.Ghf).Equals(stem, StringComparison.OrdinalIgnoreCase))
                {
                    return landscape.Ghf;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The landscapes, water and mist the .engine_level the scenario .list names states.
    /// </summary>
    /// <remarks>
    /// `sub_82ABAEF0` walks the file's records: type 4 (0x82abb1ec) is a landscape .ehf by full
    /// path, made a landscape entity for every record alike; type 5 (0x82abb358) a .water,
    /// type 32 (0x82abb384) a .mist. Each scenario's file names its own scenario folder, so the
    /// paths are taken literally — matching vista files by leaf name across every .engine_level
    /// in the banks drew Bowerstone's chapter-3 vistas in the default scenario. A landscape's
    /// grid is the .ghf of the same stem the scenario .list names (Level_LoadHeightfieldGhfEntry
    /// 0x8250ECE0 loads every one listed). Without a stated .engine_level this falls back to the
    /// discovered heightfield and its siblings.
    /// </remarks>
    private static StatedWorld ReadStatedWorld(
        LevelEntry level, ScenarioManifest? manifest, Func<string, byte[]?>? read, string heightfieldPath)
    {
        var world = new StatedWorld();
        EngineLevelFile? engineLevel = null;
        if (manifest is not null && read is not null)
        {
            foreach (string line in manifest.EngineLevel)
            {
                if (Engine_LevelManifest.TryResolvePath(line, read, out string path) &&
                    read(path) is { } bytes &&
                    Engine_EngineLevelFile.Parse(bytes) is { Heightfields.Count: > 0 } parsed)
                {
                    engineLevel = parsed;
                    break;
                }
            }
        }

        if (engineLevel is null || manifest is null || read is null)
        {
            world.Landscapes.Add(new StatedLandscape(level.MainEhfPath, heightfieldPath));
            foreach (string adjacent in level.AdjacentEhfPaths)
            {
                world.Landscapes.Add(new StatedLandscape(adjacent, string.Empty));
            }

            foreach ((string extension, List<string> into) in
                     new[] { (".water", world.Water), (".mist", world.Mist) })
            {
                foreach (string owner in new[] { level.MainEhfPath, heightfieldPath })
                {
                    string candidate = owner.Length != 0 ? ReplaceExtension(owner, extension) : string.Empty;
                    if (candidate.Length != 0 && (read is null || read(candidate) is not null))
                    {
                        into.Add(candidate);
                        break;
                    }
                }
            }

            return world;
        }

        var grids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in manifest.Ghf)
        {
            if (Engine_LevelManifest.TryResolvePath(line, read, out string ghf))
            {
                grids[StemOf(ghf)] = ghf;
            }
        }

        if (heightfieldPath.Length != 0)
        {
            grids.TryAdd(StemOf(heightfieldPath), heightfieldPath);
        }

        bool mainStated = false;
        foreach (EngineLevelHeightfield heightfield in engineLevel.Heightfields)
        {
            string ehf = Engine_LevelManifest.VirtualPath(heightfield.Path);
            string ghf = grids.TryGetValue(StemOf(ehf), out string? stated) ? stated : string.Empty;
            var landscape = new StatedLandscape(ehf, ghf);
            if (!mainStated && ghf.Length != 0 &&
                ghf.Equals(heightfieldPath, StringComparison.OrdinalIgnoreCase))
            {
                world.Landscapes.Insert(0, landscape);
                mainStated = true;
            }
            else
            {
                world.Landscapes.Add(landscape);
            }
        }

        if (!mainStated && heightfieldPath.Length != 0)
        {
            world.Landscapes.Insert(0, new StatedLandscape(level.MainEhfPath, heightfieldPath));
        }

        foreach (string water in engineLevel.WaterPaths)
        {
            world.Water.Add(Engine_LevelManifest.VirtualPath(water));
        }

        foreach (string mist in engineLevel.MistPaths)
        {
            world.Mist.Add(Engine_LevelManifest.VirtualPath(mist));
        }

        int gridCount = world.Landscapes.FindAll(l => l.Ghf.Length != 0).Count;
        GD.Print(
            $"[level] {level}: .engine_level states {world.Landscapes.Count} landscape(s) " +
            $"({gridCount} with a stated grid), {world.Water.Count} water, {world.Mist.Count} mist");
        return world;
    }

    private static string StemOf(string path)
    {
        int cut = path.LastIndexOfAny(new[] { '/', '\\' });
        string leaf = cut < 0 ? path : path[(cut + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot < 0 ? leaf : leaf[..dot];
    }

    /// <summary>
    /// A landscape with no stated grid, from the .ehf's own patches: one mesh per file, so it
    /// takes its own splat material and its own type-4 bake.
    /// </summary>
    private static TerrainMesh? BuildPatches(LevelEntry level, string ehfPath, Func<string, byte[]?> source)
    {
        if (source(ehfPath) is not { } bytes)
        {
            GD.Print($"[level] {level}: landscape {ehfPath} is not in any bank");
            return null;
        }

        if (!Engine_EhfPatches.TryBuild(bytes, out List<EhfVistaPatch> patches, out string why))
        {
            GD.Print($"[level] {level}: landscape {ehfPath} skipped ({why})");
            return null;
        }

        var meshes = new List<TerrainMesh>();
        foreach (EhfVistaPatch patch in patches)
        {
            if (patch.Mesh.TriangleCount != 0)
            {
                meshes.Add(patch.Mesh);
            }
        }

        // The PATCHES of one heightfield do fuse — they share its material and its bake.
        return meshes.Count != 0 ? Concatenate(meshes) : null;
    }

    private static TerrainMesh Concatenate(List<TerrainMesh> meshes)
    {
        int vertices = 0;
        int indices = 0;
        foreach (TerrainMesh mesh in meshes)
        {
            vertices += mesh.VertexCount;
            indices += mesh.Indices.Length;
        }

        var positions = new float[vertices * 3];
        var normals = new float[vertices * 3];
        var uvs = new float[vertices * 2];
        var merged = new int[indices];

        int vertexBase = 0;
        int indexCursor = 0;
        float lowest = float.PositiveInfinity;
        float highest = float.NegativeInfinity;

        foreach (TerrainMesh mesh in meshes)
        {
            int count = mesh.VertexCount;
            Array.Copy(mesh.Positions, 0, positions, vertexBase * 3, count * 3);
            Array.Copy(mesh.Normals, 0, normals, vertexBase * 3, count * 3);
            Array.Copy(mesh.Uvs, 0, uvs, vertexBase * 2, count * 2);

            foreach (int index in mesh.Indices)
            {
                merged[indexCursor++] = index + vertexBase;
            }

            vertexBase += count;
            lowest = MathF.Min(lowest, mesh.MinHeight);
            highest = MathF.Max(highest, mesh.MaxHeight);
        }

        return new TerrainMesh
        {
            Width = 0,
            Height = 0,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = merged,
            MinHeight = float.IsFinite(lowest) ? lowest : 0f,
            MaxHeight = float.IsFinite(highest) ? highest : 0f,
        };
    }

    private static bool EnsureDirectory(LevelEntry level, out string error)
    {
        error = string.Empty;

        string scene = level.ScenePath;
        string directory = scene[..scene.LastIndexOf('/')];

        Error made = DirAccess.MakeDirRecursiveAbsolute(directory);
        if (made is Error.Ok or Error.AlreadyExists)
        {
            return true;
        }

        error = $"could not create {directory}: {made}";
        return false;
    }
}
