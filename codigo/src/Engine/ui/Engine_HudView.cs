using System.Collections.Generic;
using Godot;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The rebuilt HUD, drawn by Godot from a layout, the artist's pieces and the guest's numbers.
/// <see cref="Engine_HudLayout"/> says where a piece goes, <see cref="Engine_HudArt"/> what it
/// looks like, <see cref="Engine_HudValues"/> what it reads (by name). Nothing here mirrors an
/// element tree.
///
/// Pieces are placed every frame, not once: the anchor resolves against the visible canvas,
/// which changes with the window.
/// </summary>
[Tool]
public sealed partial class Engine_HudView : Node3D
{
    private Engine_HudModPieces? _modPieces;

    public Engine_HudLayout Layout { get; set; } = Engine_HudLayout.Shipped();

    public Engine_HudArt Art { get; set; } = new();

    /// <summary>
    /// The guest's published values. A second seat's view SHARES the first view's values — the
    /// guest publishes one set for the one `hud.bgf` presentation — and leaves reading and gliding
    /// them to their owner (<see cref="ReadsValues"/>), or the glides would advance twice a frame.
    /// </summary>
    public Engine_HudValues Values { get; init; } = new();

    /// <summary>False on a view that shares another view's <see cref="Values"/>.</summary>
    public bool ReadsValues { get; init; } = true;

    /// <summary>
    /// Where this view draws: the whole window, or one player's half on split screen. Player
    /// one's view moves between <see cref="Engine_UiSeats.Seat.Whole"/> and
    /// <see cref="Engine_UiSeats.Seat.PlayerOne"/> as the screen splits; player two's view is
    /// <see cref="Engine_UiSeats.Seat.PlayerTwo"/> for its whole life and is player one's HUD keyed
    /// to player two (<see cref="ForPlayerTwo"/>).
    /// </summary>
    public Engine_UiSeats.Seat Seat { get; set; }

    /// <summary>
    /// Which players' views the timing meter stands in while split: bit 0 player one, bit 1
    /// player two — whose pip the guest armed. Ignored on the whole window.
    /// </summary>
    public int ExpressionPlayers { get; set; } = 1;

    /// <summary>Whose expression wheel is open (the guest's wheel+24): 1 or 2. Split screen draws
    /// it in that player's half only.</summary>
    public int WheelPlayer { get; set; } = 1;

    /// <summary>
    /// The whole window belongs to ONE player (an online session): the partner's pieces — the
    /// guest's co-op health and second will column — stay off it. Only the shared co-op screen,
    /// where one window serves both players, shows them.
    /// </summary>
    public bool HidesPartner { get; set; }

    /// <summary>
    /// This machine's player is player two (an online joiner). The guest's one HUD still states
    /// both heroes by slot — hero one's health and will as `PlayerHealth`/`SpellCharging`, hero
    /// two's as `CoopHealth`/`SpellCharging2` — and hero one is the HOST here, so the joiner's
    /// own numbers are player two's. The view is drawn for player two (<see cref="ForPlayerTwo"/>);
    /// a change rebuilds it.
    /// </summary>
    public bool LocalPlayerTwo
    {
        get => _localPlayerTwo;
        set
        {
            if (_localPlayerTwo == value)
            {
                return;
            }

            _localPlayerTwo = value;
            if (_builtOnce)
            {
                Build();
            }
        }
    }

    private bool _localPlayerTwo;

    private bool _builtOnce;

    /// <summary>
    /// The view is player two's: their half on split screen, or the joiner's whole window. It is
    /// player one's HUD — the same pieces, art and layout — keyed to player two: a player-one
    /// piece with a player-two source (<see cref="Engine_HudLayout.PlayerTwoSourceOf"/>) takes
    /// that source's element state and values, and the source is not drawn.
    /// </summary>
    private bool ForPlayerTwo => Seat == Engine_UiSeats.Seat.PlayerTwo || LocalPlayerTwo;

    /// <summary>Whether a piece is drawn in this view's seat.</summary>
    private bool Admits(Engine_HudLayout.Piece piece)
    {
        if (Seat == Engine_UiSeats.Seat.Whole)
        {
            Engine_UiSeats.Seat whose = Engine_HudLayout.OwnerOf(piece);
            if (LocalPlayerTwo)
            {
                return whose != Engine_UiSeats.Seat.PlayerTwo;
            }

            return !HidesPartner || whose != Engine_UiSeats.Seat.PlayerTwo;
        }

        if (Engine_HudLayout.IsExpressionMeter(piece))
        {
            return (ExpressionPlayers & (Seat == Engine_UiSeats.Seat.PlayerTwo ? 2 : 1)) != 0;
        }

        if (Engine_HudLayout.SharedOnSplit(piece) || Engine_HudLayout.EachPlayerOnSplit(piece))
        {
            return true;
        }

        if (Engine_HudLayout.IsExpressionWheel(piece))
        {
            return (WheelPlayer == 2) == (Seat == Engine_UiSeats.Seat.PlayerTwo);
        }

        Engine_UiSeats.Seat owner = Engine_HudLayout.OwnerOf(piece);
        return Seat == Engine_UiSeats.Seat.PlayerTwo
            ? owner == Engine_UiSeats.Seat.PlayerOne
            : owner != Engine_UiSeats.Seat.PlayerTwo;
    }

    /// <summary>
    /// A view for player two holds player one's own pieces (keyed to player two) and not their
    /// player-two sources; player two's half adds only the screen's pieces both halves show.
    /// </summary>
    private bool Builds(Engine_HudLayout.Piece piece)
    {
        if (!ForPlayerTwo)
        {
            return true;
        }

        Engine_UiSeats.Seat owner = Engine_HudLayout.OwnerOf(piece);
        if (owner != Engine_UiSeats.Seat.Whole)
        {
            return owner == Engine_UiSeats.Seat.PlayerOne && Layout.PlayerTwoSourceOf(piece) is not null;
        }

        return Seat != Engine_UiSeats.Seat.PlayerTwo ||
            Engine_HudLayout.IsExpressionMeter(piece) ||
            Engine_HudLayout.IsExpressionWheel(piece) ||
            Engine_HudLayout.SharedOnSplit(piece) ||
            Engine_HudLayout.EachPlayerOnSplit(piece);
    }

    /// <summary>This view's seat on the current <see cref="Window"/>.</summary>
    private Engine_UiSeats.Frame SeatFrame => Engine_UiSeats.Of(Window, Seat);

    /// <summary>Whether the node carries a seat transform that a return to the whole window must undo.</summary>
    private bool _seated;

    /// <summary>The shipped look, for text pieces.</summary>
    public Engine_UiTheme? Theme { get; set; }

    /// <summary>
    /// The mods' <c>ui/hud.json</c> files to build from, LOWEST priority first; null reads the
    /// enabled mods (<see cref="Engine_HudModPieces.LoadFromMods"/>). The Mod Studio's preview
    /// states the mod being edited here.
    /// </summary>
    public IReadOnlyList<(string Mod, string Text)>? ModFiles { get; set; }

    /// <summary>The mods' own pieces, once built — an editor's preview sets their stand-in values.</summary>
    public Engine_HudModPieces? ModPieces => _modPieces;

    /// <summary>Every built shipped piece and copy, by id, with its node — for an editor's hit test.</summary>
    public IEnumerable<(string Id, Node3D Node)> DrawnPieces()
    {
        foreach (Built built in _built)
        {
            if (GodotObject.IsInstanceValid(built.Node) && built.Node.IsVisibleInTree())
            {
                yield return (built.Piece.Id, built.Node);
            }
        }
    }

    /// <summary>Resolves a stated texture hash to a loaded texture, for the pieces' own art.</summary>
    public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

    /// <summary>The element pipeline for `hud.bgf`, supplied by the owner each sample.</summary>
    public Engine_UiElementResolve? Resolve { get; set; }

    /// <summary>The guest's chosen texture per element, as last sampled.</summary>
    public uint[]? LiveTextures { get; set; }

    /// <summary>
    /// The guest's stated per-element transforms — the third channel the clone binding takes,
    /// beside the draw gate and the texture. Required for a meter to fill: `ApplyTransform` falls
    /// back to the authored scale, and every bar in `hud.bgf` is authored `scale.x = 0`, so null
    /// re-zeroes the bar each frame over the meter scrub.
    /// </summary>
    public IReadOnlyList<float?[]?>? LiveTransform { get; set; }

    /// <summary>
    /// The guest's live element text, by ordinal — what a piece flagged
    /// <see cref="Engine_HudLayout.Piece.LiveText"/> paints on its own text elements. The
    /// expression wheel's eight titles are written by `sub_826AB1F8` onto the elements, not
    /// through the text registry the other pieces read.
    /// </summary>
    public string?[]? LiveTexts { get; set; }

    /// <summary>
    /// The window size the anchors resolve against. Fed by the owner: this node lives inside a
    /// SubViewport whose own size is the window's, which breaks a fixed-size editor preview.
    /// </summary>
    public Vector2 Window { get; set; } = new(1280f, 720f);

    /// <summary>
    /// The guest's per-element active bits, indexed by document ordinal — the state gate. A
    /// derived piece exists only while the guest lights its source element. Null (no sample yet)
    /// hides gated pieces: "not stated" must not render as "on".
    /// </summary>
    public bool[]? ElementActive { get; set; }

    /// <summary>
    /// The two inputs the frame's <see cref="Resolve"/> was made from, beside it: the guest's
    /// active bit per element and its stated opacity (null where unstated) — after the owner's
    /// fold released what its slides command. A copy that states a slide of its own
    /// (<see cref="Built.SlideOverrides"/>) resolves itself from these with its slide in place of
    /// the frame's. Null (the editor, a lab): the authored state stands for them.
    /// </summary>
    public IReadOnlyList<bool>? ActiveInputs { get; set; }
    public IReadOnlyList<float?>? StatedOpacity { get; set; }

    /// <summary>
    /// The guest's stated slide per element — the same list the resolve folds. A group whose
    /// current slide is authored `Off` or `TurnOff…` has been switched OFF by the guest's slide
    /// toggle (`sub_823FE848` → GotoSlide), whatever the fold reads for its opacity: the fade-out
    /// slides COMMAND opacity 100 and animate it to 0, and a rebuilt group has no timeline to
    /// finish that on, so the quest box sat at 100/50 as a dark band across the top for good.
    /// </summary>
    public IReadOnlyList<int>? LiveSlides { get; set; }

    /// <summary>The name of the slide the guest states for an element, or empty.</summary>
    private string LiveSlideName(int ordinal)
    {
        if (Screen is not { } screen || LiveSlides is null || ordinal < 0 ||
            ordinal >= LiveSlides.Count || LiveSlides[ordinal] <= 0)
        {
            return string.Empty;
        }

        // Slide 0 is the master; the guest's numbering counts the rest from one.
        int wanted = LiveSlides[ordinal];
        int seen = 0;
        foreach (BgfSlide slide in screen.Document.Slides)
        {
            if (slide.OwnerElement == ordinal && !slide.IsMaster && ++seen == wanted)
            {
                return slide.Name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The parsed document, for the playhead tracks. Bars, fills and slide-ins are authored
    /// animations — keyframes over position/scale/opacity — scrubbed by a meter value:
    /// t = value x 10 on the element's 0..1000-tick timeline.
    /// </summary>
    public Engine_UiScreen? Screen { get; set; }

    /// <summary>One playhead track bound to a node of a piece's clone.</summary>
    /// <param name="Value">A nested meter's own handle inside a group piece; null = the piece's.</param>
    /// <param name="Pulse">Whether that nested value is a pulser phase rather than a meter.</param>
    private sealed record BoundTrack(
        BgfAnimation Animation, Node3D Node, Engine_UiTimeline.Target Target, float Base,
        Engine_HudValues.Handle? Value = null, bool Pulse = false);

    /// <summary>
    /// One element's time window on a scrubbed timeline, at its clone-binding slot. The value
    /// fields match <see cref="BoundTrack"/>'s.
    /// </summary>
    private sealed record BoundWindow(
        int Slot, Engine_UiTimeline.Window Window,
        Engine_HudValues.Handle? Value = null, bool Pulse = false);

    /// <summary>One built piece, with what it needs to be driven and placed.</summary>
    private sealed class Built
    {
        public required Engine_HudLayout.Piece Piece { get; init; }
        public required Node3D Node { get; init; }

        /// <summary>
        /// A mod clone's original (<see cref="Engine_HudModPieces.Clone"/>), or null. A copy is
        /// its original's player's: it is admitted to that player's seat and stands where the
        /// original stands, its own offset kept.
        /// </summary>
        public Engine_HudLayout.Piece? Origin { get; set; }

        /// <summary>The piece whose owner rules apply: the original for a clone.</summary>
        public Engine_HudLayout.Piece Rules => Origin ?? Piece;

        /// <summary>
        /// A keyed piece's elements to their player-two source's (<see cref="KeyOrdinals"/>); null
        /// for a piece that shows its own.
        /// </summary>
        public Dictionary<int, int>? Keys { get; set; }

        /// <summary>The element whose live state stands for <paramref name="ordinal"/>: its source when keyed, -1 for none.</summary>
        public int StateOf(int ordinal) =>
            Keys is null ? ordinal : Keys.TryGetValue(ordinal, out int source) ? source : -1;

        /// <summary>The element whose live state gates the whole piece.</summary>
        public int StateOrdinal => StateOf(Piece.ElementOrdinal);

        /// <summary>The authored animation tracks this piece's meter scrubs.</summary>
        public List<BoundTrack> Tracks { get; } = new();

        /// <summary>The time windows the same scrub opens and closes.</summary>
        public List<BoundWindow> Windows { get; } = new();

        /// <summary>The clone's text elements, for a piece that paints live element text.</summary>
        public List<(MeshInstance3D Node, int Ordinal)> LiveTextNodes { get; } = new();

        /// <summary>
        /// The expression meter's own nodes, resolved once: the two sweet-spot bars by name and
        /// the three pip anchors by binding.
        /// </summary>
        /// <remarks>
        /// Resolved once because the search is the cost: `Engine_UiTheme.FindByName` recurses
        /// through `GetChildren()`, which allocates a `Godot.Collections.Array` per node visited.
        ///
        /// The tree is a clone that is built once and never re-parented, so the nodes cannot
        /// move. `PipsResolved` rather than a null test, so anchors the file genuinely lacks are
        /// not searched for again every tick.
        /// </remarks>
        public Node3D? BarLeft { get; set; }

        public Node3D? BarRight { get; set; }

        public Node3D?[] Pips { get; } = new Node3D?[3];

        /// <summary>Each pip's authored rest position, taken the first frame it is driven.</summary>
        public Vector3?[] PipRest { get; } = new Vector3?[3];

        public bool PipsResolved { get; set; }

        /// <summary>The band last tinted onto the bars, so an unchanged band re-tints nothing.</summary>
        public int TintedBand { get; set; } = int.MinValue;

        /// <summary>What each of those last painted, keyed by ordinal.</summary>
        public Dictionary<int, string> ShownTexts { get; } = new();

        /// <summary>
        /// The string the FILE authors for each text node, keyed by ordinal — kept so a label
        /// the guest never writes can still say what it was authored to say. See the paint
        /// loop for why this is not a general fallback.
        /// </summary>
        public Dictionary<int, string> AuthoredTexts { get; } = new();

        /// <summary>
        /// The piece's own width in canvas units at scale 1, measured once. A bar scales from its
        /// left edge while a quad is centred on its origin, so the width is what lets the node be
        /// shifted back to keep the left edge put.
        /// </summary>
        public float Width { get; init; }

        public Engine_HudValues.Handle Value { get; init; }

        /// <summary>
        /// The authored basis, kept so the canvas factor multiplies it rather than replacing it:
        /// the per-frame write must be absolute or it compounds, while keeping the piece's size.
        /// </summary>
        public Basis Authored { get; init; } = Basis.Identity;

        /// <summary>A mod's size factor for this piece, on top of the canvas scale.</summary>
        public float ExtraScale { get; set; } = 1f;

        /// <summary>
        /// A mod clone's meter sources by meter name (<see cref="Engine_HudModPieces.Clone"/>): its
        /// nested meters read these instead of the guest's. Null for every shipped piece.
        /// </summary>
        public Dictionary<string, string>? Remap { get; set; }

        /// <summary>
        /// A COPY'S OWN SLIDES (<see cref="Engine_HudModPieces.Clone.Slides"/>): element ordinal
        /// → the slide this copy stands on in place of the frame's — a fader the shipped game never
        /// raises, raised for the copy alone. Null for every shipped piece and every copy that
        /// follows its original's state.
        /// </summary>
        public Dictionary<int, int>? SlideOverrides { get; set; }

        /// <summary>The copy's own resolve (<see cref="SlideOverrides"/>), made from the frame's inputs with its slides in place; null when it follows the frame's.</summary>
        public Engine_UiElementResolve? OwnResolve { get; set; }

        /// <summary>The slide list the copy's own resolve was last applied with (the frame's with the overrides), reused across frames.</summary>
        public int[]? OwnSlides { get; set; }
        public bool[]? OwnActive { get; set; }
        public float?[]? OwnStated { get; set; }

        /// <summary>The <see cref="Engine_HudModMeters.Generation"/> a meter copy was built for; -1 for a piece that is not one.</summary>
        public int MeterGeneration { get; set; } = -1;

        /// <summary>The mod meter a copy shows (<see cref="MeterGeneration"/>), for the readout.</summary>
        public string MeterId { get; set; } = string.Empty;

        /// <summary>
        /// THE RAISE SLIDE'S END STATE OVER THE COPY'S SUBTREE: every element under an overridden
        /// owner switched on (the resolve's "a later setActive" input), except the ones that slide
        /// names inactive — a meter element inside the copy owns a timeline of its own whose master
        /// marks it inactive until the guest's own scrub lights it, and here the scrub's windows gate
        /// it instead. The subtree is listed once per copy; the flags are re-laid each resolve.
        /// </summary>
        public bool[]? OwnSwitched { get; set; }
        public List<int>? OwnSubtree { get; set; }

        /// <summary>
        /// A copy placed by its ART rather than its element's origin: the shift (the node's own
        /// units, before the canvas scale) that puts the art's top edge on the placed point and
        /// its centre on the placed x — the crucible's bar is authored 250 units above its
        /// container, the generic's 1 below. Zero for every other piece.
        /// </summary>
        public Vector3 ArtShift { get; set; }

        /// <summary>The art's measured size (the node's own units), for the readout.</summary>
        public Vector2 ArtSize { get; set; }

        /// <summary>A clone's own copies of its icon's materials, painted with <see cref="Icon"/>.</summary>
        public List<ShaderMaterial> IconMaterials { get; } = new();

        /// <summary>The icon's candidate textures, first that loads wins; empty once one has.</summary>
        public List<string> IconCandidates { get; } = new();

        /// <summary>The loaded icon, re-applied after the binding paints its own each frame.</summary>
        public Texture2D? Icon { get; set; }

        /// <summary>
        /// A clone's bar elements: the node, the guest meter the original's fill follows, the
        /// mod source the copy's fill follows, and the bar's full-length scale once measured.
        /// </summary>
        public List<ClonedBar> Bars { get; } = new();

        /// <summary>
        /// This piece's clone bound to the elements that drive it — the same join and per-frame
        /// apply the owned books use. Without it a piece shows the file's placeholder: where the
        /// game chooses art at runtime the `.bgf` holds one stand-in for the whole set and the
        /// guest states a texture per element.
        /// </summary>
        public Engine_UiCloneBinding Binding { get; } = new();

        /// <summary>The last string drawn, so glyphs are only re-laid when the text changes.</summary>
        public string Shown { get; set; } = string.Empty;

        /// <summary>An editor's stand-in for a widget (<see cref="BuildWidgetStandIn"/>): placed, never driven.</summary>
        public bool StandsIn { get; init; }
    }

    private readonly List<Built> _built = new();

    /// <summary>The mods' runtime meters (<see cref="Engine_HudModMeters"/>) as pieces of this view; built on first use.</summary>
    private Engine_HudModPieces? _modMeters;

    /// <summary>The <see cref="Engine_HudModMeters.Generation"/> the meter pieces were built from.</summary>
    private int _modMetersGeneration = -1;

    /// <summary>
    /// The meters a call put on the HUD (<c>meter</c> on every surface), as mod pieces of this
    /// view: rebuilt when the set changes, their values read live. A view for player two builds
    /// player two's; a shared couch screen (one window, local co-op) both players'.
    /// </summary>
    private void DriveModMeters(Engine_UiSeats.Frame frame)
    {
        int generation = Engine_HudModMeters.Generation;
        if (_modMeters is null)
        {
            if (Engine_HudModMeters.Count == 0 || Engine.IsEditorHint())
            {
                return;
            }

            _modMeters = new Engine_HudModPieces { Name = "ModMeters", FindPiece = FindDrawn };
            AddChild(_modMeters);
        }

        _modMeters.Window = frame.Window;
        if (generation == _modMetersGeneration)
        {
            return;
        }

        _modMetersGeneration = generation;
        bool shared = Seat == Engine_UiSeats.Seat.Whole && !LocalPlayerTwo && Refii.Host.CoopSession.LocalCoop;
        _modMeters.Theme = Theme;
        _modMeters.StringBook = Values.Strings;
        _modMeters.Sources = Sources with
        {
            StandIn = source => Engine_HudModMeters.Resolve(source) ?? StandIn?.Invoke(source),
        };
        _modMeters.ForPlayerTwo = ForPlayerTwo;
        _modMeters.Build(Engine_HudModMeters.PiecesFor(ForPlayerTwo ? 2 : 1, shared));
        BuildMeterCopies(shared, generation);
    }

    /// <summary>One bar element of a mod clone — see <see cref="Built.Bars"/>.</summary>
    private sealed class ClonedBar
    {
        public required Node3D Node { get; init; }
        public required Engine_HudValues.Handle Guest { get; init; }
        public required string Source { get; init; }

        /// <summary>The element's x scale at a full meter, learned from the original's fill.</summary>
        public float? Full { get; set; }
    }

    /// <summary>The last published-channel report, so it prints on change rather than once.</summary>

    /// <summary>
    /// Builds every piece the layout names. Safe to call again — it rebuilds from scratch.
    /// </summary>
    public void Build()
    {
        // The layout from the document AS IT STANDS: a mod that changed `hud.bgf`'s structure, or
        // a mod set whose `layout` blocks changed, is a new arrangement, not the old one rebuilt.
        if (Derive?.Invoke() is { } derived)
        {
            Screen = derived.Screen;
            Layout = derived.Layout;
        }

        foreach (Built built in _built)
        {
            built.Node.QueueFree();
        }

        _built.Clear();
        _builtOnce = true;
        _sources = null;
        // The meter copies went with the pieces: built again on the next drive.
        _modMetersGeneration = -1;
        BindCentreToggle();

        // What mods put on the HUD, and which shipped pieces they replace (not built).
        Engine_HudModPieces.ModLayout modLayout = ModFiles is { } files
            ? Engine_HudModPieces.LoadFrom(files)
            : Engine_HudModPieces.LoadFromMods();
        ModLayout = modLayout;

        // Pieces mods define (ui/hud.json), beside the ones cut from hud.bgf; children of this view
        // so they show and hide with the HUD. A piece that FOLLOWS a shipped piece is that
        // piece's player's and is drawn wherever it is (FindDrawn); a free piece is the screen's
        // and stays out of player two's half.
        bool ownsMods = Seat != Engine_UiSeats.Seat.PlayerTwo;
        if (_modPieces is null)
        {
            _modPieces = new Engine_HudModPieces { Name = "ModPieces" };
            _modPieces.FindPiece = FindDrawn;
            AddChild(_modPieces);
        }

        if (_modPieces is not null)
        {
            _modPieces.Window = SeatFrame.Window;
            _modPieces.Theme = Theme;
            _modPieces.StringBook = Values.Strings;
            _modPieces.Sources = Sources;
            _modPieces.ForPlayerTwo = ForPlayerTwo;
            _modPieces.Build(ownsMods
                ? modLayout.Pieces
                : modLayout.Pieces.FindAll(piece => piece.Follow.Length != 0));
        }

        if (ownsMods && modLayout.Hidden.Count != 0)
        {
            GD.Print($"[hud-mods] shipped piece(s) replaced by mods: {string.Join(", ", modLayout.Hidden)}");
        }

        var missing = new List<string>();
        int unplaced = 0;

        foreach (Engine_HudLayout.Piece piece in Layout.Pieces)
        {
            // A widget is drawn by its own reader (Engine_HudWidgets); the editor's preview stands
            // one in so it can be seen, picked and moved there.
            if (Engine_HudWidgets.IsWidget(piece.Id))
            {
                if (Engine.IsEditorHint() && ownsMods && !modLayout.Hidden.Contains(piece.Id))
                {
                    BuildWidgetStandIn(piece, piece.Id, modLayout);
                    foreach (Engine_HudModPieces.Clone clone in modLayout.Clones)
                    {
                        if (clone.Of == piece.Id)
                        {
                            BuildWidgetStandIn(piece with { Id = clone.Id, Offset = piece.Offset + clone.Offset }, clone.Id, modLayout);
                        }
                    }
                }

                continue;
            }

            // The piece mods address: in a view for player two, player one's piece stands for
            // its player-two source, so a mod's statement about player two's widget (hide, scale,
            // copies) lands on it, and one about player one's does not.
            Engine_HudLayout.Piece? source = ForPlayerTwo ? Layout.PlayerTwoSourceOf(piece) : null;
            string declared = source?.Id ?? piece.Id;
            if (modLayout.Hidden.Contains(declared))
            {
                continue;
            }

            // Inventoried, not drawn: `hud.bgf` binds 110 elements and states where only some go;
            // the rest are authored at their component's origin, so building them would stack
            // pieces at dead centre.
            if (!piece.Placed)
            {
                ++unplaced;
                continue;
            }

            if (!Builds(piece))
            {
                continue;
            }

            if (BuildPiece(piece, missing, source) is not { } built)
            {
                continue;
            }

            built.ExtraScale = modLayout.Scales.TryGetValue(declared, out float factor) ? factor : 1f;

            // Mods' copies of this piece, built right after it from the same art, in every view
            // that builds the original. A copy of player two's widget is built from player one's
            // in a view for player two, keyed as the original is. A copy of player one's widget
            // that reads per-player values is EACH player's (Engine_HudModTwins.EachPlayer): it
            // is built beside player two's widget too, its meters named as that widget's.
            foreach (Engine_HudModPieces.Clone stated in modLayout.Clones)
            {
                Engine_HudModPieces.Clone clone = stated;
                if (stated.Of != declared)
                {
                    if (!Engine_HudModTwins.EachPlayer(stated) ||
                        Engine_HudLayout.PlayerTwoSourceId(stated.Of) != declared ||
                        modLayout.Clones.Exists(c => c.Id == Engine_HudModTwins.PlayerTwoId(stated.Id)))
                    {
                        continue;
                    }

                    clone = Engine_HudModTwins.TwinForPlayerTwo(stated, declared, MeterNamesToTwo(stated.Of, declared));
                }

                Engine_HudLayout.Piece copy = piece with { Id = clone.Id, Offset = piece.Offset + clone.Offset };
                if (BuildPiece(copy, missing, source) is { } cloned)
                {
                    cloned.Origin = piece;
                    cloned.ExtraScale = modLayout.Scales.TryGetValue(clone.Id, out float own) ||
                        modLayout.Scales.TryGetValue(stated.Id, out own) ? own : built.ExtraScale;
                    ApplyClone(cloned, clone);
                    GD.Print($"[hud-mods] '{clone.Id}' ({clone.Mod}) is a copy of '{piece.Id}'" +
                        (source is { } keyed ? $" keyed to '{keyed.Id}'" : string.Empty));
                }
            }
        }

        FinishBuild(missing, unplaced);
        Rebuilt?.Invoke(this);
    }

    /// <summary>
    /// Re-derives the layout (and the document it is cut from) at the head of every
    /// <see cref="Build"/>; null keeps the one set. The game's owner states it, so a remount that
    /// changes `hud.bgf` or a mod's <c>layout</c> rebuilds the arrangement from the new files.
    /// </summary>
    /// <remarks>An answer of null keeps the layout set (the owner has just derived it).</remarks>
    public System.Func<(Engine_UiScreen Screen, Engine_HudLayout Layout)?>? Derive { get; set; }

    /// <summary>What the mods stated at the last <see cref="Build"/>: hidden, scaled, copied and added pieces.</summary>
    public Engine_HudModPieces.ModLayout? ModLayout { get; private set; }

    /// <summary>Raised at the end of every <see cref="Build"/>, for the owner of the widgets the view does not draw.</summary>
    public event System.Action<Engine_HudView>? Rebuilt;

    /// <summary>
    /// The value sources' readers for this view (<see cref="Engine_UiValueSources"/>): the guest's
    /// published meters and texts as THIS view holds them (player two's view states player two's
    /// own over the shared feed), mod values, the player store and the local hero. An editor's
    /// preview adds its stand-ins.
    /// </summary>
    public Engine_UiValueSources.Context Sources
    {
        get
        {
            if (_sources is null)
            {
                _sources = new Engine_UiValueSources.Context
                {
                    Player = ForPlayerTwo ? 2 : 1,
                    Mod = key => !Engine.IsEditorHint() && Refii.Host.HostBridge.TryGetModValue(key, out float value) ? value : null,
                    GameMeter = name => Values.Meter(Engine_HudValues.Handle.For(name)),
                    GameText = name => Values.Text(Engine_HudValues.Handle.For(name)),
                    PlayerValue = (player, key) => Engine.IsEditorHint() ? null : Refii.Host.HostBridge.GetPlayerValue(player, key),
                    Hero = key =>
                    {
                        if (Engine.IsEditorHint())
                        {
                            return null;
                        }

                        Refii.Host.HostBridge.HeroVitals vitals = Refii.Host.HostBridge.GetHeroVitals();
                        return !vitals.Known || vitals.MaxHealth <= 0f ? null
                            : key == "health" ? vitals.Health : vitals.MaxHealth;
                    },
                    // The mod meters' values (mod:meter.<id>.value / .max) first: a copy of the
                    // game's own meter art reads its bar through them (Engine_HudModMeters).
                    StandIn = source => Engine_HudModMeters.Resolve(source) ?? StandIn?.Invoke(source),
                    StandInText = source => StandInText?.Invoke(source),
                };
            }

            return _sources;
        }
    }

    private Engine_UiValueSources.Context? _sources;

    /// <summary>An editor preview's stand-in for a whole source text (<c>"player:souls.stamina"</c>); null for none.</summary>
    public System.Func<string, float?>? StandIn { get; set; }

    /// <summary>An editor preview's stand-in for a <c>game:</c> text field; null for none.</summary>
    public System.Func<string, string?>? StandInText { get; set; }

    /// <summary>The readers for one built piece: player two's for a piece of player two's (<see cref="Engine_UiValueSources.PlayerFor"/>).</summary>
    private Engine_UiValueSources.Context SourcesFor(Built built) =>
        Sources.ForPlayer(Engine_UiValueSources.PlayerFor(ForPlayerTwo,
            Engine_HudLayout.OwnerOf(built.Rules) == Engine_UiSeats.Seat.PlayerTwo));

    /// <summary>
    /// An editor-only stand-in for a widget its own reader draws in the game: a frame of the
    /// widget's nominal size at its place, so the preview shows, picks and drags it like a piece.
    /// </summary>
    private void BuildWidgetStandIn(Engine_HudLayout.Piece piece, string id, Engine_HudModPieces.ModLayout modLayout)
    {
        Engine_HudWidgets.Widget? widget = Engine_HudWidgets.Find(piece.Id) ??
            (modLayout.Clones.Find(c => c.Id == id) is { } clone ? Engine_HudWidgets.Find(clone.Of) : null);
        if (widget is null)
        {
            return;
        }

        float factor = modLayout.Scales.TryGetValue(id, out float stated) && stated > 0f ? stated : 1f;
        Vector2 size = widget.Size * factor;
        var frame = new Engine_UiRoundedPanel { Name = id.Replace('.', '_') };
        frame.Style(new Color(0.55f, 0.85f, 1f, 0.18f), new Color(0.55f, 0.85f, 1f, 0.9f), 1.5f);
        frame.Corner(6f);
        AddChild(frame);
        var padding = new Vector2(Engine_UiRoundedPanel.PaddingX, Engine_UiRoundedPanel.PaddingY);
        Vector2 inner = size - padding * 2f;
        frame.Fit(new Rect2(-inner * 0.5f, inner), 0.4f);
        _built.Add(new Built { Piece = piece with { Id = id }, Node = frame, StandsIn = true, ExtraScale = factor });
    }

    /// <summary>
    /// A clone's differences from its original: its meters' sources, its hue, its picture. Every
    /// material it changes is DUPLICATED first — a clone's art shares materials with the original
    /// it was cut from, and recolouring the shared one would turn the health bar blue too.
    /// </summary>
    private void ApplyClone(Built built, Engine_HudModPieces.Clone clone)
    {
        built.Remap = clone.Values;

        foreach ((string element, string meter) in clone.Bars)
        {
            if (built.Node.FindChild(element, recursive: true, owned: false) is Node3D bar &&
                clone.Values.TryGetValue(meter, out string? source))
            {
                built.Bars.Add(new ClonedBar
                {
                    Node = bar,
                    Guest = Engine_HudValues.Handle.For(meter),
                    Source = source,
                });
            }
            else
            {
                GD.PushWarning($"[hud-mods] '{clone.Id}': no element '{element}' or no value for meter '{meter}'");
            }
        }

        if (clone.Hue != 0f)
        {
            foreach (string name in clone.HueElements)
            {
                if (built.Node.FindChild(name, recursive: true, owned: false) is { } element)
                {
                    foreach (ShaderMaterial material in OwnMaterials(element))
                    {
                        material.SetShaderParameter(HueShiftName, clone.Hue);
                    }
                }
            }
        }

        if (clone.IconElement.Length != 0 &&
            built.Node.FindChild(clone.IconElement, recursive: true, owned: false) is { } icon)
        {
            built.IconMaterials.AddRange(OwnMaterials(icon));
            foreach (string candidate in clone.IconTextures)
            {
                string path = candidate.StartsWith("game://", System.StringComparison.Ordinal)
                    ? candidate["game://".Length..]
                    : candidate;
                built.IconCandidates.Add(path.Replace('\\', '/').ToLowerInvariant());
            }
        }

        // THE COPY'S OWN SLIDES: an element of the copy (its own root, or one beneath it) stood
        // on a slide of the copy's choosing — a fader the game never raises, raised here. The
        // original's ordinal is not touched; the copy resolves itself (ResolvedFor).
        if (clone.Slides is { Count: > 0 } slides && Screen is { } screen && built.Piece.ElementOrdinal >= 0)
        {
            built.SlideOverrides = new Dictionary<int, int>();
            foreach ((string element, int slide) in slides)
            {
                int found = -1;
                for (int i = 0; i < screen.Nodes.Length; ++i)
                {
                    if (screen.Nodes[i].Name == element &&
                        (i == built.Piece.ElementOrdinal || InSubtree(screen, i, built.Piece.ElementOrdinal)))
                    {
                        found = i;
                        break;
                    }
                }

                if (found < 0)
                {
                    GD.PushWarning($"[hud-mods] '{clone.Id}': no element '{element}' under '{clone.Of}' to put on slide {slide}");
                    continue;
                }

                built.SlideOverrides[found] = slide;
            }
        }
    }

    /// <summary>Copy art hud.bgf does not bind, warned once.</summary>
    private readonly HashSet<string> _missingCopyArt = new(System.StringComparer.Ordinal);

    /// <summary>
    /// The merged bounds of every mesh under <paramref name="root"/>, in <paramref name="root"/>'s
    /// own space (each mesh's AABB through the local transforms between), or null with no mesh.
    /// </summary>
    private static Rect2? MergedBounds(Node3D root)
    {
        Rect2? merged = null;
        Walk(root, Transform3D.Identity);
        return merged;

        void Walk(Node node, Transform3D toRoot)
        {
            for (int i = 0, count = node.GetChildCount(); i < count; ++i)
            {
                if (node.GetChild(i) is not Node3D spatial)
                {
                    continue;
                }

                Transform3D local = toRoot * spatial.Transform;
                if (spatial is MeshInstance3D { Mesh: not null } mesh)
                {
                    Aabb box = local * mesh.GetAabb();
                    var rect = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
                    merged = merged is { } so ? so.Merge(rect) : rect;
                }

                Walk(spatial, local);
            }
        }
    }

    /// <summary>
    /// THE MOD METERS IN THE GAME'S OWN ART (<see cref="Engine_HudModMeters.CopiesFor"/>): for
    /// every meter of a copy style, the piece hud.bgf binds for that art — the fader element's
    /// container, cloned WHOLE (nothing stripped: its bar is the part the copy drives) — built as
    /// a group piece at the meter's stack place, its bar's <c>MeterName</c> remapped to the
    /// meter's value, its fader's raise slide stated for the copy alone. Rebuilt whenever the
    /// meters change (<paramref name="generation"/>); the earlier copies go first.
    /// </summary>
    private void BuildMeterCopies(bool shared, int generation)
    {
        for (int i = _built.Count - 1; i >= 0; --i)
        {
            Built old = _built[i];
            if (old.MeterGeneration < 0)
            {
                continue;
            }

            if (GodotObject.IsInstanceValid(old.Node))
            {
                old.Node.QueueFree();
            }

            _built.RemoveAt(i);
        }

        if (Engine.IsEditorHint())
        {
            return;
        }

        var missing = new List<string>();
        foreach (Engine_HudModMeters.Copy copy in Engine_HudModMeters.CopiesFor(ForPlayerTwo ? 2 : 1, shared))
        {
            Engine_HudLayout.Piece? original = null;
            foreach (Engine_HudLayout.Piece candidate in Layout.Pieces)
            {
                if (candidate.Id == copy.Style.CloneOf)
                {
                    original = candidate;
                    break;
                }
            }

            if (original is not { } source)
            {
                if (_missingCopyArt.Add(copy.Style.CloneOf))
                {
                    GD.PushWarning($"[hud-mods] meter '{copy.Meter.Id}': hud.bgf binds no '{copy.Style.CloneOf}' to copy; " +
                        $"nothing is drawn for style '{copy.Style.Name}'");
                }

                continue;
            }

            Engine_HudLayout.Piece piece = source with
            {
                Id = copy.Record.Id,
                Anchor = Engine_HudLayout.Anchor.TopCentre,
                Offset = copy.Offset,
                Strip = null,
                Depth = 0f,
                Placed = true,
            };
            if (BuildPiece(piece, missing) is not { } built)
            {
                continue;
            }

            built.Origin = source;
            built.MeterGeneration = generation;
            built.MeterId = copy.Meter.Id;
            ApplyClone(built, copy.Record);

            // BY THE ART: the clone's merged bounds in the node's own units — the art's top edge
            // goes on the stack place, its centre on the stack's x, whatever the element's origin.
            if (MergedBounds(built.Node) is { } bounds)
            {
                built.ArtSize = new Vector2(bounds.Size.X, bounds.Size.Y);
                built.ArtShift = new Vector3(-(bounds.Position.X + bounds.Size.X * 0.5f), -bounds.End.Y, 0f);
            }

            GD.Print($"[hud-mods] meter '{copy.Meter.Id}' is a copy of '{source.Id}' ({copy.Style.Name}), lit on slide " +
                $"{copy.Style.RaiseSlide} of its own");
        }

        if (missing.Count != 0)
        {
            GD.PushWarning($"[hud-mods] meter copies with no art: {string.Join(", ", missing)}");
        }

        // A fresh walk places and resolves them.
        _refreshed = false;
    }

    /// <summary>
    /// The resolve <paramref name="built"/> reads this walk: the frame's, or — for a copy with
    /// <see cref="Built.SlideOverrides"/> — its own, applied from the frame's inputs
    /// (<see cref="LiveSlides"/>, <see cref="ActiveInputs"/>, <see cref="StatedOpacity"/>) with the
    /// copy's slides in place, each slide's entries released over the stated attributes exactly as
    /// the feed releases the slides it folds. Re-applied on the frames the feed changes
    /// (<paramref name="read"/>); the slide pass itself runs only when a slide differs.
    /// </summary>
    private Engine_UiElementResolve? ResolvedFor(Built built, bool read)
    {
        if (built.SlideOverrides is not { Count: > 0 } overrides || Screen is not { } screen || screen.Document is not { } file)
        {
            return Resolve;
        }

        int count = screen.Nodes.Length;
        bool fresh = built.OwnResolve is null || built.OwnSlides is null || built.OwnSlides.Length != count;
        if (fresh)
        {
            built.OwnResolve = new Engine_UiElementResolve(screen);
            built.OwnSlides = new int[count];
            built.OwnActive = new bool[count];
            built.OwnStated = new float?[count];
            built.OwnSwitched = new bool[count];
            built.OwnSubtree = new List<int>();
            foreach ((int ordinal, int _) in overrides)
            {
                for (int i = 0; i < count; ++i)
                {
                    if (i == ordinal || InSubtree(screen, i, ordinal))
                    {
                        built.OwnSubtree.Add(i);
                    }
                }
            }
        }
        else if (!read)
        {
            return built.OwnResolve;
        }

        int[] slides = built.OwnSlides!;
        bool[] active = built.OwnActive!;
        float?[] stated = built.OwnStated!;
        bool[] switched = built.OwnSwitched!;
        for (int i = 0; i < count; ++i)
        {
            slides[i] = LiveSlides is { } live && i < live.Count ? live[i] : 0;
            active[i] = ActiveInputs is not { } bits || i >= bits.Count || bits[i];
            stated[i] = StatedOpacity is { } opacity && i < opacity.Count ? opacity[i] : null;
            switched[i] = false;
        }

        // The subtree on: the slide's end state for everything under the owner (Built.OwnSwitched).
        foreach (int i in built.OwnSubtree!)
        {
            switched[i] = true;
            active[i] = true;
        }

        foreach ((int ordinal, int slide) in overrides)
        {
            if (ordinal < 0 || ordinal >= count)
            {
                continue;
            }

            slides[ordinal] = slide;
            if (Engine_UiElementResolve.SlideOf(file, ordinal, slide) is { } own)
            {
                Engine_UiElementResolve.ReleaseSlideEntries(file, own, active, stated);
                foreach (BgfSlideEntry entry in own.Entries)
                {
                    if (!entry.Active && entry.ElementOrdinal >= 0 && entry.ElementOrdinal < count)
                    {
                        switched[entry.ElementOrdinal] = false;
                    }
                }
            }
        }

        built.OwnResolve!.Apply(slides, active, StatedOpacity is null ? null : stated, switched);
        return built.OwnResolve;
    }

    /// <summary>
    /// The copies on their own slides as they stand — the meter copies, and every hud.json clone
    /// that states <c>slides</c> — one line each, for the debug channel and the HUD meter lab:
    /// the copy's id and original, whether it is drawn, its own resolve's answer at its root (drawn,
    /// opacity, why dark) against the frame's, and where it is placed.
    /// </summary>
    public IEnumerable<string> DescribeMeterCopies()
    {
        foreach (Built built in _built)
        {
            if (built.MeterGeneration < 0 && built.SlideOverrides is null)
            {
                continue;
            }

            int root = built.StateOrdinal;
            string own = built.OwnResolve is { } mine && root >= 0 && root < mine.Drawn.Length
                ? $"own drawn={mine.Drawn[root]} opacity={mine.Opacity[root]:0.00}" + (mine.Drawn[root] ? string.Empty : $" dark: {mine.WhyDark(root)}")
                : "own resolve: none";
            string frame = Resolve is { } shared && root >= 0 && root < shared.Drawn.Length
                ? $"frame drawn={shared.Drawn[root]} opacity={shared.Opacity[root]:0.00}"
                : "frame resolve: none";
            var overrideList = new List<string>();
            if (built.SlideOverrides is { } o)
            {
                foreach ((int ordinal, int slide) in o)
                {
                    overrideList.Add($"{ordinal}->{slide}");
                }
            }

            string overrides = overrideList.Count == 0 ? "none" : string.Join(",", overrideList);
            var trackList = new List<string>();
            foreach (BoundTrack track in built.Tracks)
            {
                string time = track.Value is { } handle && NestedTime(built, handle, track.Pulse) is { } tick ? tick.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "none";
                trackList.Add($"{track.Target}@{track.Node.Name}#{built.Binding.OrdinalOf(track.Node)}(scale {track.Node.Scale.X:0.00},{track.Node.Scale.Y:0.00} visible={track.Node.Visible}) value='{track.Value?.Name}' t={time}");
            }

            var stateList = new List<string>();
            if (built.OwnResolve is { } resolve && Screen is { } page && root >= 0)
            {
                for (int ordinal = root; ordinal < System.Math.Min(root + 24, page.Nodes.Length); ++ordinal)
                {
                    if (ordinal != root && !InSubtree(page, ordinal, root))
                    {
                        break;
                    }

                    stateList.Add($"{ordinal}:{page.Nodes[ordinal].Name} drawn={resolve.Drawn[ordinal]} active={resolve.Active[ordinal]} slid={resolve.Slid[ordinal]} opacity={resolve.Opacity[ordinal]:0.00}");
                }
            }

            yield return $"  state: {string.Join("; ", stateList)}";

            yield return $"  tracks: {string.Join("; ", trackList)}";
            yield return $"copy '{built.Piece.Id}' of '{built.Origin?.Id}' meter '{built.MeterId}': visible={built.Node.Visible} " +
                $"slides[{overrides}] {own}; {frame}; at {built.Node.Position} tracks={built.Tracks.Count} " +
                $"art {built.ArtSize} shift {built.ArtShift}";
        }
    }

    /// <summary>
    /// A clone's bars at the MOD's values. The binding has just written the original's stated
    /// fill (the guest's `scale.x` on the bar element), which is the health meter's — so the full
    /// length is read off it (`scale ÷ meter%`, remembered once known) and the copy's bar is
    /// redrawn at its own meter.
    /// </summary>
    private void RescaleClonedBars(Built built)
    {
        foreach (ClonedBar bar in built.Bars)
        {
            Transform3D transform = bar.Node.Transform;
            Vector3 column = transform.Basis.X;
            float stated = column.Length();
            if (Values.Meter(bar.Guest) is { } guest && guest > 1f && stated > 0.0001f)
            {
                bar.Full = stated / (guest / 100f);
            }

            if (bar.Full is not { } full || Engine_UiValueSources.Ratio(bar.Source, SourcesFor(built)) is not { } ours)
            {
                continue;
            }

            Vector3 direction = stated > 0.0001f ? column / stated : Vector3.Right;
            Basis basis = transform.Basis;
            basis.X = direction * (full * ours / 100f);
            bar.Node.Transform = new Transform3D(basis, transform.Origin);
        }
    }

    /// <summary>
    /// A clone's icon: resolved LATE and retried, because the HUD is built before the game's own
    /// mount (mods folded in) becomes the texture loader's first resolver — it is installed by a
    /// deferred call after mounting, and a mod's texture asked for before that is not there.
    /// The bytes are probed first so a retry never trips Godot's load errors.
    /// </summary>
    private static void PaintCloneIcon(Built built)
    {
        if (built.Icon is null && built.IconCandidates.Count != 0 &&
            Engine.GetProcessFrames() % 30 == 0)
        {
            foreach (string candidate in built.IconCandidates)
            {
                if (Refii.Textures.Engine_BnkTextureLoader.ReadBytes(candidate) is null ||
                    ResourceLoader.Load<Texture2D>(Refii.Textures.Engine_BnkTextureLoader.PathPrefix + candidate)
                        is not { } picture)
                {
                    continue;
                }

                built.Icon = picture;
                built.IconCandidates.Clear();

                // The WHOLE picture: the original may show a window of a shared sheet, and a
                // replacement is one image.
                foreach (ShaderMaterial material in built.IconMaterials)
                {
                    material.SetShaderParameter(Uv1OffsetName, Vector2.Zero);
                    material.SetShaderParameter(Uv1ScaleName, Vector2.One);
                }

                GD.Print($"[hud-mods] '{built.Piece.Id}' icon: {candidate}");
                break;
            }
        }

        if (built.Icon is { } icon)
        {
            foreach (ShaderMaterial material in built.IconMaterials)
            {
                material.SetShaderParameter(DiffuseTexName, icon);
            }
        }
    }

    /// <summary>The subtree's shader materials, each replaced by its own copy.</summary>
    private static List<ShaderMaterial> OwnMaterials(Node node)
    {
        var owned = new List<ShaderMaterial>();
        if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial shared)
        {
            var copy = Engine_UiSceneBuilder.CopyMaterial(shared);
            mesh.MaterialOverride = copy;
            owned.Add(copy);
        }

        for (int i = 0, count = node.GetChildCount(); i < count; ++i)
        {
            owned.AddRange(OwnMaterials(node.GetChild(i)));
        }

        return owned;
    }

    /// <summary>
    /// The meter and pulser names player two's widget <paramref name="twoId"/> binds where player
    /// one's <paramref name="oneId"/> binds each of its own (`PlayerHealth` → `CoopHealth`,
    /// `PlayerLowHealthPulser` → `CoopLowHealthPulser`): the elements paired as
    /// <see cref="KeyOrdinals"/> pairs them, each binding to the one of its kind on the paired
    /// element. Empty without the document.
    /// </summary>
    private Dictionary<string, string> MeterNamesToTwo(string oneId, string twoId)
    {
        var names = new Dictionary<string, string>(System.StringComparer.Ordinal);
        Engine_HudLayout.Piece? one = null;
        Engine_HudLayout.Piece? two = null;
        foreach (Engine_HudLayout.Piece candidate in Layout.Pieces)
        {
            one = candidate.Id == oneId ? candidate : one;
            two = candidate.Id == twoId ? candidate : two;
        }

        if (Screen is not { } screen || Bindings is not { } bindings || one is not { } p1 || two is not { } p2 ||
            p1.ElementOrdinal < 0 || p2.ElementOrdinal < 0 ||
            p1.ElementOrdinal >= screen.Nodes.Length || p2.ElementOrdinal >= screen.Nodes.Length)
        {
            return names;
        }

        Dictionary<int, int> keys = KeyOrdinals(screen.Nodes[p1.ElementOrdinal], screen.Nodes[p2.ElementOrdinal]);
        foreach (Engine_UiBindings.Binding binding in bindings.All)
        {
            if (!keys.TryGetValue(binding.ElementOrdinal, out int paired))
            {
                continue;
            }

            foreach (Engine_UiBindings.Binding candidate in bindings.All)
            {
                if (candidate.ElementOrdinal == paired && candidate.Kind == binding.Kind)
                {
                    names[binding.Name] = candidate.Name;
                    break;
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Pairs a player-one widget's elements with its player-two copy's: the same element by name
    /// under paired parents, then the copy's renamed ones in order (`HealthAndMana` /
    /// `CoopHealthAndMana`). An element the copy lacks (the column's `charged` rings) is left out.
    /// </summary>
    private static Dictionary<int, int> KeyOrdinals(UiNode own, UiNode source)
    {
        var keys = new Dictionary<int, int>();
        Pair(own, source, keys);
        return keys;

        static void Pair(UiNode own, UiNode source, Dictionary<int, int> keys)
        {
            keys[own.Ordinal] = source.Ordinal;
            var left = new List<UiNode>(source.Children);
            var renamed = new List<UiNode>();
            foreach (UiNode child in own.Children)
            {
                int at = left.FindIndex(candidate => candidate.Name == child.Name);
                if (at < 0)
                {
                    renamed.Add(child);
                    continue;
                }

                Pair(child, left[at], keys);
                left.RemoveAt(at);
            }

            for (int i = 0; i < renamed.Count && i < left.Count; ++i)
            {
                Pair(renamed[i], left[i], keys);
            }
        }
    }

    /// <summary>
    /// The drawn node of a shipped piece, for a mod piece that follows it. In a view for player
    /// two, player two's widget is drawn as player one's piece, and player one's own is not here.
    /// </summary>
    private Node3D? FindDrawn(string id)
    {
        string drawn = id;
        if (ForPlayerTwo)
        {
            if (Engine_HudLayout.PlayerOneIdOf(id) is { } one)
            {
                drawn = one;
            }
            else if (Engine_HudLayout.PlayerTwoSourceId(id) is not null)
            {
                return null;
            }
        }

        foreach (Built built in _built)
        {
            if (built.Piece.Id == drawn && GodotObject.IsInstanceValid(built.Node))
            {
                return built.Node;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds one piece's node, binding and tracks; null (and named) when it has no art. With a
    /// <paramref name="source"/>, the piece is keyed to it (<see cref="ForPlayerTwo"/>).
    /// </summary>
    private Built? BuildPiece(
        Engine_HudLayout.Piece piece, List<string> missing, Engine_HudLayout.Piece? source = null)
    {
        {
            float scale = Engine_HudLayout.Scale(SeatFrame.Extent);

            Node3D? node = piece.Driven == Engine_HudLayout.Drive.Text
                ? new MeshInstance3D { Name = piece.Id.Replace('.', '_') }
                : Art.Build(piece, new Vector2(160f * scale, 16f * scale));

            if (node is null)
            {
                // Named, not counted: art in neither the banks nor a mod folder means the layout
                // refers to something that does not exist.
                missing.Add($"{piece.Id}({piece.Art})");
                return null;
            }

            AddChild(node);
            NoShadow(node);
            var built = new Built
            {
                Piece = piece,
                Node = node,
                Width = Measure(node),
                Value = Engine_HudValues.Handle.For(piece.Value),
                // The ancestors' rotation and scale, which the art (the element's own basis)
                // does not carry.
                Authored = (piece.Ancestor == default ? Basis.Identity : piece.Ancestor) *
                    node.Transform.Basis,
            };

            if (Screen is { } document)
            {
                built.Binding.ResolveTexture = ResolveTexture;
                built.Binding.Bind(node, document);
                if (source is { } from && piece.ElementOrdinal >= 0 && from.ElementOrdinal >= 0 &&
                    piece.ElementOrdinal < document.Nodes.Length && from.ElementOrdinal < document.Nodes.Length)
                {
                    built.Keys = KeyOrdinals(document.Nodes[piece.ElementOrdinal], document.Nodes[from.ElementOrdinal]);
                    built.Binding.Key(document, built.Keys);
                }

                if (piece.LiveText)
                {
                    built.LiveTextNodes.AddRange(built.Binding.TextNodes(document));
                    foreach ((MeshInstance3D _, int ordinal) in built.LiveTextNodes)
                    {
                        if (ordinal >= 0 && ordinal < document.Nodes.Length &&
                            document.Nodes[ordinal].Text?.Display is { Length: > 0 } authored)
                        {
                            built.AuthoredTexts[ordinal] = authored;
                        }
                    }
                }
            }

            BindTracks(built);
            _built.Add(built);
            return built;
        }
    }

    private void FinishBuild(List<string> missing, int unplaced)
    {
        // What the containment pass kept and dropped: a container is placed with the subtrees of
        // its separately-drawn children removed.
        var housings = new List<string>();
        foreach (Built b in _built)
        {
            if (b.Piece.Strip is { Count: > 0 } held)
            {
                housings.Add($"{b.Piece.Id}-[{string.Join(',', held)}]");
            }
        }

        if (housings.Count != 0)
        {
            GD.Print($"[hud] {housings.Count} container(s) placed with their live children " +
                $"stripped: {string.Join("; ", housings)}");
        }

        int tracks = 0;
        int scrubbed = 0;
        foreach (Built built in _built)
        {
            tracks += built.Tracks.Count;
            if (built.Tracks.Count > 0)
            {
                ++scrubbed;
            }
        }

        GD.Print($"[hud] built {_built.Count}/{Layout.Pieces.Count} piece(s); {Art.Describe()}; " +
            $"{scrubbed} meter piece(s) scrub {tracks} authored track(s)" +
            (unplaced == 0
                ? string.Empty
                : $"; {unplaced} bound piece(s) inventoried but UNPLACED — " +
                  $"see {Engine_HudLayout.SnapshotPath} to position them") +
            (missing.Count == 0 ? string.Empty : $"; NO ART: {string.Join(", ", missing)}"));
    }

    /// <summary>A built piece's width in canvas units, from whatever it actually became.</summary>
    private static float Measure(Node3D node) =>
        node is MeshInstance3D { Mesh: not null } mesh ? mesh.GetAabb().Size.X : 0f;

    /// <summary>
    /// Binds a meter piece's authored animation tracks to the nodes of its clone. A track lives on
    /// an element of the piece's subtree and targets position, scale or opacity. The track for the
    /// piece's root element carries the authored base of its animated axis: the layout owns where
    /// the piece sits, so the scrub applies the delta from the authored parked value.
    /// </summary>
    /// <summary>
    /// The element `screencentertoggle.lua` moves, and how far. `setCenterOn` (`main.1` pc4..21)
    /// reads `centerXPos` off the Behavior element and writes it to its parent's `position.x`,
    /// remembering the old x; `setCenterOff` (`main.2`) restores it. `SetDPadCenter(bool)` raises
    /// those two events. The layout places the parent absolutely every frame, so the latch is a
    /// conditional offset: the authored-to-centre delta in canvas units, resolved at build.
    /// </summary>
    private int _centreMover = -1;

    private float _centreDelta;

    private void BindCentreToggle()
    {
        _centreMover = -1;
        _centreDelta = 0f;
        if (Screen is not { } screen)
        {
            return;
        }

        foreach (UiNode node in screen.Nodes)
        {
            if (node.Type != BgfElementType.Behavior || node.Name != "CenterToggle" ||
                node.Parent is not { } mover ||
                !node.Element.TryGetAttribute("centerXPos", out BgfAttribute centre))
            {
                continue;
            }

            _centreMover = mover.Ordinal;
            _centreDelta = centre.AsFloat() - mover.PositionX;
            GD.Print($"[hud] CenterToggle on '{mover.Name}' (ord {mover.Ordinal}): " +
                $"centred x {centre.AsFloat()} = authored {mover.PositionX} {_centreDelta:+0.#;-0.#}");
            return;
        }
    }

    private Engine_UiBindings? _bindings;

    /// <summary>The HUD document's name bindings, read once — the fold of stated toggles needs them.</summary>
    public Engine_UiBindings? Bindings
    {
        get
        {
            if (_bindings is null && Screen is { } screen)
            {
                _bindings = Engine_UiBindings.Read(screen.Document);
            }

            return _bindings;
        }
    }

    /// <summary>
    /// The timing meter's pip: which `PrimDescName` binding carries it and its angle along the
    /// arc in DEGREES, or null while no pip travels. See <see cref="Engine_UiHudExpressionMeter"/>.
    /// </summary>
    public (string Binding, float Degrees)? ExpressionPip { get; set; }

    /// <summary>Which pip elements the built HUD actually contains, and which it does not.</summary>
    public static readonly System.Collections.Generic.HashSet<string> PipAnchorsFound = new();

    public static readonly System.Collections.Generic.HashSet<string> PipAnchorsMissing = new();

    /// <summary>The meter's colour band while it is on (0 low, 1 medium, 2 high), else -1.</summary>
    public int ExpressionBand { get; set; } = -1;

    /// <summary>Each pip element's authored position, so the swing below is absolute rather than compounding.</summary>

    /// <summary>
    /// The arc's radius, STATED BY THE GUEST: `flt_8209F790` = 0x43200000 = 160.0, loaded at
    /// 0x823D1080 into lane 2 of the point `sub_823D0F10` rotates. It is not measured off the
    /// art and not ours to choose.
    /// </summary>
    private const float PipRadius = 160f;

    /// <summary>
    /// The pip element the guest names for a given `PrimDescName` binding, inside this piece's
    /// clone — or null when the file binds nothing under it.
    /// </summary>
    /// <remarks>
    /// The file states which element is the hero's marker. `hud.bgf` authors three elements
    /// under the meter's `Group` — `white_pip` #1516, `black_pip` #1519, `blackwhite_pip` #1522
    /// — each carrying `PrimDescName` "HeroPip" / "HenchmanPip" / "BothPip", each with its own
    /// 16-unit quad off `art\gui\persistant\expressionmeter.tex`, authored at opacity 0. The
    /// guest resolves the same three names out of GUI registry 11 (`sub_823D0E10`:
    /// `sub_822A9C60(container[11], hash)`), so the binding is the join; no host-side table of
    /// node names. The builder builds all three (e.g.
    /// `hud/Component_0/Layer/ExpressionMeter/Group/white_pip`).
    /// </remarks>
    private Node3D? PipNode(Built built, string binding)
    {
        if (Bindings is not { } bindings)
        {
            return null;
        }

        foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.PrimDesc, binding))
        {
            if (built.Binding.NodeFor(ordinal) is { } node)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>The three pip anchors, in resolve order. Static so the tick does not allocate.</summary>
    private static readonly string[] PipBindings =
    {
        Engine_UiHudExpressionMeter.HeroPip, Engine_UiHudExpressionMeter.HenchmanPip,
        Engine_UiHudExpressionMeter.BothPip,
    };

    /// <summary>
    /// Shows the travelling pip on the element the guest names: the art authored at opacity 0 is
    /// lit and swung along the arc by the guest's stated angle, and the other two stay dark.
    /// Applied after the clone binding, which writes the guest's stated opacity 0 back.
    /// </summary>
    private void DrivePip(Built built)
    {
        // Resolved once — see Built.BarLeft. The clone is built once and never re-parented, so
        // a node that was found stays found; one that was not is not searched for again.
        if (!built.PipsResolved)
        {
            built.PipsResolved = true;
            built.BarLeft = Engine_UiTheme.FindByName(built.Node, "barleft") as Node3D;
            built.BarRight = Engine_UiTheme.FindByName(built.Node, "barright") as Node3D;
            for (int i = 0; i < PipBindings.Length; ++i)
            {
                built.Pips[i] = PipNode(built, PipBindings[i]);
                if (built.Pips[i] is null)
                {
                    PipAnchorsMissing.Add(PipBindings[i]);
                }
                else
                {
                    PipAnchorsFound.Add(PipBindings[i]);

                    // A private material before any write, and once is enough: the piece is a
                    // `Duplicate()` of the resident book and Godot shares a duplicate's
                    // resources, so an opacity written here would otherwise reach every other
                    // bearer of the same source quad.
                    if (built.Pips[i] is MeshInstance3D owned)
                    {
                        Engine_UiSceneBuilder.OwnQuadMaterial(owned);
                    }
                }
            }
        }

        // The sweet-spot bars take the band's colour: white art, tinted as the guest tints
        // it (`SetTintingAmountAndDiffuseColour` at amount 1). Only when the band moves —
        // `TintSubtree` recurses through `GetChildren()`.
        if (ExpressionBand >= 0 && ExpressionBand != built.TintedBand)
        {
            built.TintedBand = ExpressionBand;
            Color band = ExpressionBand switch { 0 => new Color(0f, 1f, 0f), 1 => new Color(1f, 1f, 0f), _ => new Color(1f, 0f, 0f) };
            if (built.BarLeft is { } left)
            {
                TintSubtree(left, band);
            }

            if (built.BarRight is { } right)
            {
                TintSubtree(right, band);
            }
        }

        for (int index = 0; index < PipBindings.Length; ++index)
        {
            string binding = PipBindings[index];
            Node3D? pip = built.Pips[index];
            if (pip is null || !GodotObject.IsInstanceValid(pip))
            {
                continue;
            }

            bool lit = ExpressionPip is { } state && state.Binding == binding;

            // Held on the piece, not in node metadata: a meta read is a StringName and a
            // Variant per pip per frame, each a weak handle the next GC frees in its pause.
            Vector3 rest = built.PipRest[index] ??= pip.Position;

            // The guest swings it round an arc and translates only. `sub_823D0F10` reads the
            // 0..100 position at `pip+4` (= `meter+16`, one base down — the tick is handed
            // `meter+12`), turns it into DEGREES and then RADIANS:
            //
            //     823d104c  lfs    f10, 4(r9)          ; position 0..100
            //     823d1050  fmuls  f9, f10, f0         ; * 0.01          (flt_8209CB94)
            //     823d1054  fmsubs f8, f9, f13, f12    ; * 130.0 - 65.0  (flt_82101244/8209F794)
            //     823d1058  fmuls  f1, f8, f11         ; * pi/180        (flt_82100ED4)
            //     823d105c  bl     sub_82B4FC60        ; a Y-axis rotation matrix
            //
            // `sub_82B4FC60` builds the rotation, but the quad never wears it: the matrix is
            // applied to the constant point (0, 0, 160, 1) — `flt_8209F790` loaded at 0x823D1080
            // — and only the resulting displacement reaches the art. The loop at 0x823D1150 adds
            // it to lanes 0 and 2 of all four corner positions (`fadds f10, f0, f11` for lane 0,
            // `fadds f8, f12, f9` for lane 2) and writes nothing else, four corners, stride 0x40.
            // So the pip is a RIGID quad on a circle of radius 160 about its authored position —
            // no rotation of the art, no scale. Lane 2 is the screen vertical in the guest's GUI
            // space: the arc's apex is 160 above the pips' authored origin.
            if (lit)
            {
                float theta = Mathf.DegToRad(ExpressionPip!.Value.Degrees);
                pip.Position = new Vector3(
                    rest.X + (PipRadius * Mathf.Sin(theta)),
                    rest.Y + (PipRadius * Mathf.Cos(theta)),
                    rest.Z);
            }
            else
            {
                pip.Position = rest;
            }

            LightSubtree(pip, lit ? 1f : 0f);
        }
    }

    // ================= THE MODERN PANELS =====================================================
    //
    // The shipped HUD backs its text with black-tinted slabs (`Material655`, diffuse 0/0/0,
    // TintingAmount 1 over the `hud` blur image) — the TopBox's 780-unit bar, the counters'
    // boxes. The remaster replaces them with rounded translucent panels fitted to the text the
    // guest actually states, refit every frame, so a longer objective gets a longer panel.
    // The authored slab nodes are hidden AFTER the clone binding, which re-shows what the
    // guest's state says is drawn.

    private readonly Dictionary<string, Engine_UiRoundedPanel> _panels = new();

    /// <summary>Each group piece's placed rect, so an off-screen widget names itself.</summary>
    private readonly Dictionary<string, string> _groupSpans = new();

    private string _groupReport = string.Empty;

    // The TopBox slab: the Bar and the blur/edge pieces authored beside it.
    private static readonly string[] SlabNames = { "Bar", "blur", "left", "right", "top", "bottom" };

    /// <summary>The same names as StringNames: a node's Name compares natively, where the string form is a fresh conversion per child per walk.</summary>
    private static readonly StringName[] SlabNameNames = { "Bar", "blur", "left", "right", "top", "bottom" };

    private void DrawModernPanels()
    {
        // Reused, not rebuilt: this runs every frame.
        HashSet<string> used = _panelsUsed;
        used.Clear();

        // THE TARGETED-NPC WIDGET IS FOUR SEPARATE COMPONENTS — the name, the opinion box and
        // its slider, the mood icons and the button — each authored at its own place in
        // `hud.bgf`, which is why it reads as scattered furniture rather than one widget. It is
        // gathered across the loop and given ONE rounded panel, the way the quest box and the
        // counters are, so it wears the same look as the rest of the HUD.
        Rect2? queryBounds = null;
        float queryBehind = 1.5f;
        _queryPieces = 0;
        _queryParts.Clear();
        _queryCandidates.Clear();
        _queryTitle = null;

        Rect2? counterBounds = null;
        float counterBehind = 1.5f;

        // WHERE EVERY GROUP ACTUALLY LANDS. The health readout has been reported off-screen
        // three times and guessed at three times — from its authored position, from its anchor
        // and offset, and from the saved arrangement — and each guess said it should be on
        // screen. What was never observable is the rect it OCCUPIES once the anchor, the offset,
        // the ancestor scale and its children's own transforms have all composed. That is the
        // number that decides it, so print it. Change-keyed, so it settles to silence.
        // A diagnostic, every 30th frame: the walk crosses into every node of every group to
        // print a line that changes rarely.
        bool spanFrame = (++_groupSpanFrame % 30) == 0;
        foreach (Built built in _built)
        {
            if (!spanFrame || !built.Piece.Id.StartsWith("group.", System.StringComparison.Ordinal))
            {
                continue;
            }

            float groupBehind = 1.5f;
            Rect2? span = built.Node.Visible ? SubtreeBounds(built.Node, ref groupBehind) : null;
            _groupSpans[built.Piece.Id] = span is { } g
                ? $"{built.Piece.Id} @({built.Piece.Offset.X:0},{built.Piece.Offset.Y:0}) " +
                  $"anchor({built.Piece.Anchor.X:0.0},{built.Piece.Anchor.Y:0.0}) " +
                  $"rect {g.Position.X:0},{g.Position.Y:0} {g.Size.X:0}x{g.Size.Y:0}"
                : $"{built.Piece.Id} NOT DRAWN";
        }

        if (spanFrame)
        {
            string groups = string.Join("  |  ", _groupSpans.Values);
            if (groups != _groupReport)
            {
                _groupReport = groups;
                GD.Print($"[hud-view] groups: {groups}");
            }
        }

        foreach (Built built in _built)
        {
            if (!built.Node.Visible)
            {
                continue;
            }

            if (built.Piece.Id == "group.QuestBox.TopBox")
            {
                HideSlabs(built.Node);
                CentreTitle(built.LiveTextNodes);
                Rect2? bounds = null;
                foreach ((MeshInstance3D label, int _) in built.LiveTextNodes)
                {
                    Engine_UiHudTextStyle.Capture(label);
                    bounds = Union(bounds, TextBounds(label));
                }

                float behind = 1.5f;
                foreach ((MeshInstance3D label, int _) in built.LiveTextNodes)
                {
                    if (GodotObject.IsInstanceValid(label))
                    {
                        behind = System.Math.Min(behind, label.GlobalPosition.Z - 0.5f);
                    }
                }

                FitPanel("topbox", bounds, used, behind);
            }
            else if (built.Piece.Id.StartsWith("slidetoggle.Counter", System.StringComparison.Ordinal) ||
                built.Piece.Id.EndsWith(".CornerBox", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.CornerX.", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.CornerY.", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.SubtitlesX.", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.SubtitlesY.", System.StringComparison.Ordinal) ||
                built.Piece.Id == "slidefader2.Subtitles.Subtitles")
            {
                // The counters' strings sit in the CornerBox — a black nine-slice (`Group`#616:
                // BoxMiddle, corners, sides) sized by the CornerX/CornerY meters — and the
                // counter containers carry the flash overlay; all of it is the slab look, and
                // the strings are separate text pieces, so these pieces go whole.
                built.Node.Visible = false;
            }
            else if (built.Piece.Id.StartsWith("text.Counter", System.StringComparison.Ordinal) &&
                built.Node is MeshInstance3D counter)
            {
                // ONE BOX FOR ALL THE COUNTERS. Each line used to fit its OWN panel, keyed by
                // its piece id, so a job showing three of them — gold until promotion, the
                // multiplier, the current gold — drew three rounded panels stacked a line apart
                // and overlapping at the edges. They are one readout and they get one box, the
                // way the quest box holds its title and both body lines.
                if (TextBounds(counter) is { } line)
                {
                    counterBounds = Union(counterBounds, line);
                    counterBehind = System.Math.Min(counterBehind, counter.GlobalPosition.Z - 0.5f);
                }
            }
            else if (built.Piece.Id == "text.Subtitles.Text" && built.Node is MeshInstance3D label)
            {
                // NOT grouped with the counters: the subtitle sits elsewhere on the screen and
                // is a different readout, so it keeps its own box.
                FitPanel(built.Piece.Id, TextBounds(label), used, label.GlobalPosition.Z - 0.5f);
            }
            else if (built.Piece.Id.Contains(".TargetedQuery", System.StringComparison.Ordinal))
            {
                // AND THE SHIPPED SLAB GOES, exactly as the quest box's does. The citizen
                // readout authors its own black band — `BoxAndBar/TopBox` with the same
                // `Bar`/`blur`/`left`/`right`/`top` children, spanning x -641.7..+641.7 at
                // opacity 70 — which is a rule across the whole screen, not a panel. Replacing
                // it with the rounded panel is the entire point of `Engine_UiRoundedPanel`;
                // leaving it drew both.
                //
                // This also retires the reason the distance heuristic below exists: the union
                // used to come out 1269 units wide because the band was in it. With the band
                // hidden and the widget cut as one group, the subtree's own extent is the
                // answer and nothing has to be guessed from proximity.
                HideSlabs(built.Node);

                // THE PROMPTS SIT UNDER THE HEART, at the bar's right end. Our layout; only x
                // moves, since their y already places them below the bar.
                //
                // (The `.bgf` happens to put them at x 386. That is the old renderer's layout
                // and is not a constraint — we take ART and STRINGS from that file and place
                // things ourselves. This belongs in the layout template once the HUD editor
                // covers this widget; it is here only until then.)
                CentreUnderBar(built.Node);

                // ONE TEXT STYLE. The name is a title, the button's label is body — the same cut
                // the display box makes on a path ending "Title". The style is metadata the
                // re-mesh reads, so the shown text is dropped to force one repaint through it;
                // after that the piece repaints only when the guest changes the string.
                foreach ((MeshInstance3D text, int ordinal) in built.LiveTextNodes)
                {
                    if (!GodotObject.IsInstanceValid(text) || text.HasMeta(StyledMeta))
                    {
                        continue;
                    }

                    // THE ELEMENT DECIDES, not only the piece. While this widget was cut per
                    // binding the name had a piece of its own with "Title" in its id; cut whole
                    // there is one id for the lot, so the name would have taken body style with
                    // everything else. The element is called `TitleText` either way.
                    bool isTitle =
                        built.Piece.Id.Contains("Title", System.StringComparison.OrdinalIgnoreCase) ||
                        text.Name.ToString().Contains("Title", System.StringComparison.OrdinalIgnoreCase);
                    text.SetMeta(StyledMeta, true);
                    text.SetMeta("text_size",
                        isTitle ? Engine_UiHudTextStyle.TitleSize : Engine_UiHudTextStyle.BodySize);
                    text.SetMeta("text_colour",
                        isTitle ? Engine_UiHudTextStyle.TitleColour : Engine_UiHudTextStyle.BodyColour);
                    built.ShownTexts.Remove(ordinal);
                }

                // THE WIDGET IS UP ONLY WHILE THE GUEST NAMES SOMEONE.
                //
                // Nothing else here is a reliable "is it up" signal: the gift, divorce and
                // marry icons ride registry 9 and the slider rides registry 10, neither of
                // which this host states at all yet, so those elements stay drawn whether or
                // not a citizen is targeted — and a panel fitted to them sits on screen for
                // ever with nothing in it.
                //
                // The guest states it OUTRIGHT, so ask that rather than infer. The record's
                // `+0` byte is the readout's raise flag — `sub_823FD8E8` stores 1, `sub_823FD9B0`
                // stores 0 — and `Engine_UiHudTargetedQuery` now states it on this very channel.
                //
                // The previous gate read the shown TITLE and was wrong in a way that only a run
                // shows: `ShownTexts` is what we last PAINTED, and a re-text is skipped when the
                // guest states an empty string, so the last citizen's name stayed in that map
                // for ever and the panel never came down.
                if (Values.Visible(Engine_HudValues.Handle.For("TargetedQuery")) is not true)
                {
                    continue;
                }

                // The gauge and the icons are KEPT: they carry the opinion, which is the whole
                // point of the widget. Only the backing is replaced, and the panel is fitted to
                // everything so the parts read as one thing.
                ++_queryPieces;
                Rect2? one = SubtreeBounds(built.Node, ref queryBehind);
                _queryParts.Add(one is { } span
                    ? $"{built.Piece.Id} [{span.Position.X:0}..{span.Position.X + span.Size.X:0}]"
                    : $"{built.Piece.Id} [none]");

                // THE TITLE ANCHORS THE WIDGET, and everything that belongs to it sits near the
                // title. Unioning blind gave a rectangle 1269 units wide — the full design box —
                // which is the black band across the top of the screen rather than a panel.
                // Excluding oversized meshes was not enough: the span came from two NARROW
                // pieces at opposite edges, so the outlier has to be rejected by DISTANCE, not
                // by size. The name is the one part whose place is certain, so it is the anchor
                // and anything beyond a widget's width of it is not part of this widget.
                if (built.Piece.Id.Contains(".TargetedQueryTitle.", System.StringComparison.Ordinal))
                {
                    _queryTitle = one ?? _queryTitle;
                }

                _queryCandidates.Add(one);
            }
        }

        if (counterBounds is { } counters)
        {
            FitPanel("counters", counters.Grow(8f), used, counterBehind);
        }

        // Gathered against the title once every piece has been measured, because the title is
        // not necessarily the first of them.
        if (_queryTitle is { } anchor)
        {
            float centre = anchor.Position.X + (anchor.Size.X * 0.5f);
            foreach (Rect2? candidate in _queryCandidates)
            {
                if (candidate is not { } part)
                {
                    continue;
                }

                float partCentre = part.Position.X + (part.Size.X * 0.5f);
                if (System.Math.Abs(partCentre - centre) <= QueryGatherReach)
                {
                    queryBounds = Union(queryBounds, part);
                }
            }
        }
        else
        {
            foreach (Rect2? candidate in _queryCandidates)
            {
                queryBounds = Union(queryBounds, candidate);
            }
        }

        if (queryBounds is { } query)
        {
            // A little air, so the name and the gauge are not flush against the edge. The
            // quest box gets the same from the text bounds it fits to.
            FitPanel("targetedquery", query.Grow(8f), used, queryBehind);
        }

        // SAY WHAT THE PANEL DID. Three attempts at this widget have gone out without anyone
        // being able to see whether the panel was fitted, to what, or at what depth — the piece
        // ids and the bounds are the two things that decide it, and neither was observable.
        // Printed only when the answer changes, so it costs nothing once it settles.
        string report = queryBounds is { } r
            ? $"{_queryPieces} piece(s), rect {r.Position.X:0}, {r.Position.Y:0} {r.Size.X:0}x{r.Size.Y:0} " +
              $"at z {queryBehind:0.0} | {string.Join("  ", _queryParts)}"
            : $"{_queryPieces} piece(s), NO BOUNDS — every mesh was rejected as a container or none was drawn";
        if (_queryPieces != 0 && report != _queryReport)
        {
            _queryReport = report;
            GD.Print($"[hud-targeted] {report}");
        }

        foreach ((string key, Engine_UiRoundedPanel panel) in _panels)
        {
            if (!used.Contains(key))
            {
                panel.Visible = false;
            }
        }
    }

    private static readonly StringName OpacityParameter = "opacity";
    private static readonly StringName HueShiftName = "hue_shift";
    private static readonly StringName Uv1OffsetName = "uv1_offset";
    private static readonly StringName Uv1ScaleName = "uv1_scale";
    private static readonly StringName DiffuseTexName = "diffuse_tex";

    /// <summary>A visible, non-empty label's bounds in the HUD's space, or null.</summary>
    private static Rect2? TextBounds(MeshInstance3D label)
    {
        if (!GodotObject.IsInstanceValid(label) || label.Mesh is null || !label.IsVisibleInTree())
        {
            return null;
        }

        // An unset uniform reads back as nil, which is the shader's default 1 — a freshly
        // painted label has never had its opacity written and is fully visible.
        if (label.MaterialOverride is ShaderMaterial m &&
            m.GetShaderParameter(OpacityParameter) is { VariantType: Variant.Type.Float } op &&
            op.AsSingle() < 0.05f)
        {
            return null;
        }

        Aabb box = label.GlobalTransform * label.Mesh.GetAabb();
        if (box.Size.X <= 0f || box.Size.Y <= 0f)
        {
            return null;
        }

        return new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
    }

    /// <summary>
    /// The quest box's title, centred over its body text. Our layout: the guest writes the title
    /// box-relative at the left margin (`sub_823FC8F0`). Measured from where the title stands
    /// this frame, so a rewrite of its position by the guest is re-centred, never compounded.
    /// </summary>
    private static void CentreTitle(List<(MeshInstance3D Node, int Ordinal)> labels)
    {
        MeshInstance3D? title = null;
        Rect2? titleBounds = null;
        Rect2? body = null;
        foreach ((MeshInstance3D label, int _) in labels)
        {
            if (TextBounds(label) is not { } rect)
            {
                continue;
            }

            if (label.Name.ToString().Contains("Title", System.StringComparison.OrdinalIgnoreCase))
            {
                title = label;
                titleBounds = rect;
            }
            else
            {
                body = Union(body, rect);
            }
        }

        if (title is null || titleBounds is not { } own || body is not { } under)
        {
            return;
        }

        float shift = under.GetCenter().X - own.GetCenter().X;
        if (Mathf.Abs(shift) > 0.01f)
        {
            title.GlobalPosition += new Vector3(shift, 0f, 0f);
        }
    }

    private static Rect2? Union(Rect2? a, Rect2? b) =>
        a is null ? b : b is null ? a : a.Value.Merge(b.Value);

    private void FitPanel(string key, Rect2? bounds, HashSet<string> used, float z)
    {
        if (bounds is not { } rect)
        {
            return;
        }

        if (!_panels.TryGetValue(key, out Engine_UiRoundedPanel? panel))
        {
            panel = new Engine_UiRoundedPanel { Name = "panel_" + key.Replace('.', '_') };
            AddChild(panel);
            _panels[key] = panel;
        }

        // The bounds are measured in world space and the panel is this view's child: on a seat
        // the two differ by the seat transform, so the rectangle is brought into the view first.
        if (_seated)
        {
            Transform3D local = GlobalTransform.AffineInverse();
            Vector3 a = local * new Vector3(rect.Position.X, rect.Position.Y, z);
            Vector3 b = local * new Vector3(rect.End.X, rect.End.Y, z);
            rect = new Rect2(
                Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y),
                Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y));
            z = a.Z;
        }

        // Just behind the text it backs; the world sits far below every HUD depth.
        panel.Fit(rect, z);
        used.Add(key);
    }

    /// <summary>Marks a label already given the HUD text style, so it is styled once.</summary>
    private const string StyledMeta = "hud_text_styled";

    private int _queryPieces;
    private string _queryReport = string.Empty;

    /// <summary>Each targeted-widget piece and its horizontal span, so the outlier can be named.</summary>
    private readonly List<string> _queryParts = new();

    private readonly List<Rect2?> _queryCandidates = new();

    private Rect2? _queryTitle;

    /// <summary>
    /// How far from the name a part may sit and still belong to the targeted widget, in canvas
    /// units. The authored parts run from the mood icons out to the button, a little over four
    /// hundred either side; half the design box is the generous bound that still rejects an
    /// element parked at the screen edge.
    /// </summary>
    private const float QueryGatherReach = Engine_HudLayout.DesignWidth * 0.5f;

    /// <summary>
    /// The screen bounds of every drawn mesh under a node, and the nearest depth among them —
    /// <see cref="TextBounds"/> answers for one label, and this widget is a subtree of art.
    /// </summary>
    private static Rect2? SubtreeBounds(Node node, ref float behind)
    {
        Rect2? bounds = null;
        if (node is MeshInstance3D mesh && mesh.Mesh is not null && mesh.IsVisibleInTree())
        {
            Aabb box = mesh.GlobalTransform * mesh.Mesh.GetAabb();

            // CONTENT ONLY. A group's own root carries a quad the size of the canvas — it is the
            // container, not a drawn thing — and unioning it fitted the panel to the whole
            // screen, which is the full-width band across the top rather than a widget. Nothing
            // in this family is legitimately wider than half the design box: the opinion bar,
            // the widest part, is a little over a quarter of it.
            bool container = box.Size.X > Engine_HudLayout.DesignWidth * 0.5f ||
                box.Size.Y > Engine_HudLayout.DesignHeight * 0.5f;

            if (box.Size.X > 0f && box.Size.Y > 0f && !container)
            {
                bounds = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
                behind = System.Math.Min(behind, mesh.GlobalPosition.Z - 0.5f);
            }
        }

        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            Node child = node.GetChild(i);
            bounds = Union(bounds, SubtreeBounds(child, ref behind));
        }

        return bounds;
    }

    /// <summary>The prompt groups the targeted readout hangs off to its right.</summary>
    private static readonly string[] PromptNames = { "LeftButton", "RightButton" };

    private static readonly StringName[] PromptNameNames = { "LeftButton", "RightButton" };

    /// <summary>
    /// Each node's answer, by instance id. HUD node names never change, and reading `Name` makes
    /// a tracked StringName per call; the per-frame walks asked it of every child.
    /// </summary>
    private static readonly Dictionary<(ulong Node, StringName[] Names), bool> NameMatches = new();

    private static bool NameIn(Node node, StringName[] names)
    {
        (ulong, StringName[]) key = (node.GetInstanceId(), names);
        if (NameMatches.TryGetValue(key, out bool known))
        {
            return known;
        }

        if (NameMatches.Count >= 8192)
        {
            NameMatches.Clear();
        }

        bool found = false;
        using (StringName name = node.Name)
        {
            foreach (StringName candidate in names)
            {
                if (name == candidate)
                {
                    found = true;
                    break;
                }
            }
        }

        NameMatches[key] = found;
        return found;
    }

    /// <summary>
    /// Where the prompts sit, in canvas units from the readout's centre — under the heart at the
    /// bar's right end. The mood icons are authored at x 285 inside `grp` (scale 0.5) inside
    /// `LoveSlider` (scale 1.5), i.e. 285 x 0.75 = 214 canvas units out, and the heart rides
    /// that same end. OUR number: the file hangs the prompts at 386 with everything else on 0,
    /// and this is where they belong instead. Template material once the editor reaches here.
    /// </summary>
    private const float PromptX = 214f;

    /// <summary>
    /// How far BELOW their authored height the prompts sit, in canvas units. Ours, like
    /// <see cref="PromptX"/>: the file stacks the readout tightly, and with the prompts pulled
    /// onto the centre line the "More Info" row ended up crowding the opinion bar and the heart
    /// it now sits under. Canvas +y is up, so this subtracts.
    /// </summary>
    private const float PromptDrop = 16f;

    /// <summary>Latches a prompt's authored height, so the drop is applied once and not per frame.</summary>
    private static readonly StringName PromptBaseMeta = "hud_prompt_base_y";

    /// <summary>
    /// Brings the targeted readout's button prompts onto the widget's centre line, so they sit
    /// under the opinion bar rather than beside it. Only x moves; the authored y already places
    /// them below the bar, and the two prompts are authored at different heights so they stack
    /// rather than collide on the rare frame both are up.
    /// </summary>
    private static void CentreUnderBar(Node node)
    {
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Node child = node.GetChild(i);
            if (child is Node3D prompt && NameIn(child, PromptNameNames))
            {
                // FROM THE AUTHORED Y, NOT THE CURRENT ONE. This runs every frame, so
                // subtracting from `Position.Y` would drop the prompt another 16 units each
                // time and slide it off the bottom of the screen within a second. The authored
                // height is latched once, the first time this node is seen.
                if (!prompt.HasMeta(PromptBaseMeta))
                {
                    prompt.SetMeta(PromptBaseMeta, prompt.Position.Y);
                }

                float baseY = (float)prompt.GetMeta(PromptBaseMeta).AsDouble();
                prompt.Position = new Vector3(PromptX, baseY - PromptDrop, prompt.Position.Z);
                continue;
            }

            CentreUnderBar(child);
        }
    }

    private static void HideSlabs(Node node)
    {
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Node child = node.GetChild(i);
            if (child is Node3D spatial && NameIn(child, SlabNameNames))
            {
                spatial.Visible = false;
                continue;
            }

            HideSlabs(child);
        }
    }

    private static void TintSubtree(Node node, Color colour)
    {
        if (node is MeshInstance3D mesh)
        {
            Engine_UiSceneBuilder.SetQuadTint(mesh, colour, 1f);
        }

        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            Node child = node.GetChild(i);
            TintSubtree(child, colour);
        }
    }

    private static void LightSubtree(Node node, float opacity)
    {
        // The anchors are authored at opacity 0, so the clone binding's draw gate hides every
        // node under them each frame; the lit anchor is re-shown here, after the binding.
        if (node is Node3D spatial)
        {
            spatial.Visible = opacity > 0f;
        }

        if (node is MeshInstance3D mesh)
        {
            Engine_UiSceneBuilder.SetQuadOpacity(mesh, opacity);
        }

        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            Node child = node.GetChild(i);
            LightSubtree(child, opacity);
        }
    }

    /// <summary>
    /// The value a nested meter or pulser reads: on a keyed piece, the one its source element
    /// binds (`PlayerHealth` → `CoopHealth`, `PlayerSpellcharge1` → `HenchmanSpellcharge1`).
    /// </summary>
    private string KeyedName(Built built, Engine_UiBindings.Binding binding)
    {
        if (built.Keys is null || _bindings is null ||
            !built.Keys.TryGetValue(binding.ElementOrdinal, out int source))
        {
            return binding.Name;
        }

        foreach (Engine_UiBindings.Binding candidate in _bindings.All)
        {
            if (candidate.ElementOrdinal == source && candidate.Kind == binding.Kind)
            {
                return candidate.Name;
            }
        }

        return binding.Name;
    }

    private void BindTracks(Built built)
    {
        if (Screen is not { } screen || built.Piece.ElementOrdinal < 0)
        {
            return;
        }

        // A group piece carries its nested meters and pulsers: each scrubs the tracks its own
        // element's slides activate, against its own published value.
        if (built.Piece.Id.StartsWith("group.", System.StringComparison.Ordinal))
        {
            _bindings ??= Engine_UiBindings.Read(screen.Document);
            foreach (Engine_UiBindings.Binding binding in _bindings.All)
            {
                bool pulse = binding.Kind == Engine_UiBindings.Kind.Pulser;

                // A LAYOUT METER MUST NOT DRAG A GROUP WE PLACED. `TopBoxY` is the quest box's
                // measured height, and inside `PlayerHealth` it owns `Health`'s position.y
                // track (0 -> -136) — so the health readout slid up and down by ~90 units as
                // the quest box grew and shrank, which the diagnostic caught as its rect y
                // wandering 202 / 220 / 294 between samples.
                //
                // That coupling is the ORIGINAL's layout decision, and layout is ours: we place
                // this widget, so nothing else may move it. `Engine_UiLiveValues` already keeps
                // this same list for the same reason.
                if (Engine_UiBindings.IsLayoutMeter(binding.Name))
                {
                    continue;
                }

                if ((binding.Kind != Engine_UiBindings.Kind.Meter && !pulse) ||
                    binding.ElementOrdinal == built.Piece.ElementOrdinal ||
                    !InSubtree(screen, binding.ElementOrdinal, built.Piece.ElementOrdinal))
                {
                    continue;
                }

                BindOwnedTracks(built, screen, binding.ElementOrdinal,
                    Engine_HudValues.Handle.For(KeyedName(built, binding)), pulse);
            }

            return;
        }

        if (built.Piece.Driven != Engine_HudLayout.Drive.Bar &&
            built.Piece.Driven != Engine_HudLayout.Drive.Pulse)
        {
            return;
        }

        BindOwnedTracks(built, screen, built.Piece.ElementOrdinal, null, false);
    }

    /// <summary>
    /// Binds the tracks and time windows of an element's timeline, on the nodes of the piece's
    /// clone, scrubbed by <paramref name="value"/> (null: the piece's own value).
    /// </summary>
    private void BindOwnedTracks(
        Built built, Engine_UiScreen screen, int element, Engine_HudValues.Handle? value, bool pulse)
    {
        foreach ((BgfAnimation animation, Engine_UiTimeline.Target target) in
            Engine_UiTimeline.Tracks(screen, element))
        {
            // BY ORDINAL through the clone binding, which joined every node of the clone to its
            // element: a name is ambiguous inside a widget — the timing meter's sweet-spot
            // rotation track targets a `Group` UNDER `fill size`, and the by-name search found
            // the meter's outer `Group` first and turned the whole arc.
            bool isRoot = animation.ElementOrdinal == built.Piece.ElementOrdinal;
            Node3D? node = isRoot
                ? built.Node
                : built.Binding.NodeFor(animation.ElementOrdinal) ??
                    Engine_UiTheme.FindByName(
                        built.Node, screen.Nodes[animation.ElementOrdinal].Name) as Node3D;

            // An image element folds into its owner's quad and has no node of its own — the
            // wedge's `scalev` track targets `expressionmeter` UNDER `barleft`, and the bar
            // is the mesh. A UV track climbs to the nearest bound ancestor.
            if (node is null && target == Engine_UiTimeline.Target.UvScaleV)
            {
                for (UiNode? up = screen.Nodes[animation.ElementOrdinal].Parent;
                    up is not null && node is null; up = up.Parent)
                {
                    node = built.Binding.NodeFor(up.Ordinal);
                }
            }

            if (node is null)
            {
                continue;
            }

            UiNode doc = screen.Nodes[animation.ElementOrdinal];
            float baseline = target switch
            {
                Engine_UiTimeline.Target.PositionX => isRoot ? doc.PositionX : 0f,
                Engine_UiTimeline.Target.PositionY => isRoot ? doc.PositionY : 0f,
                _ => 0f,
            };

            built.Tracks.Add(new BoundTrack(animation, node, target, baseline, value, pulse));
        }

        int source = built.StateOf(element);
        int stated = LiveSlides is { } live && source >= 0 && source < live.Count ? live[source] : 0;
        BgfSlide? playing = Engine_UiTimeline.PlayingSlide(screen.Document, element, stated);
        foreach (Engine_UiTimeline.Window window in Engine_UiTimeline.Windows(screen, element, playing))
        {
            int slot = built.Binding.SlotFor(window.Ordinal);
            if (slot < 0)
            {
                continue;
            }

            built.Windows.Add(new BoundWindow(slot, window, value, pulse));

            // At rest until a value arrives, as the tracks are: t = 0.
            built.Binding.Gate(slot, window.IsOpen(0f));
        }
    }

    /// <summary>Opens and closes the windows that ride one value, at one tick.</summary>
    private static void GateWindows(Built built, Engine_HudValues.Handle? value, float t)
    {
        foreach (BoundWindow window in built.Windows)
        {
            if (window.Value == value)
            {
                built.Binding.Gate(window.Slot, window.Window.IsOpen(t));
            }
        }
    }

    /// <summary>
    /// A nested value's playhead, or null while the guest has not published it. A mod clone's
    /// meters read the sources it names (<c>"none"</c> holds one still); every other meter is the
    /// guest's, so a copy of the health widget still drops with it when the quest box grows.
    /// </summary>
    private float? NestedTime(Built built, Engine_HudValues.Handle handle, bool pulse)
    {
        if (built.Remap is { } remap && remap.TryGetValue(handle.Name, out string? source))
        {
            return source != "none" && Engine_UiValueSources.Ratio(source, SourcesFor(built)) is { } mod
                ? mod * 10f
                : null;
        }

        return pulse
            ? Values.Pulser(handle) is { } phase ? Mathf.Clamp(phase, 0f, 1f) * 1000f : null
            : Values.Meter(handle) is { } value ? Mathf.Clamp(value, 0f, 100f) * 10f : null;
    }

    /// <summary>
    /// Scrubs a group piece's nested meters and pulsers, each track and window at its own
    /// value's playhead. An unpublished value leaves them as they stand.
    /// </summary>
    private void ScrubNested(Built built, Vector2 at)
    {
        foreach (BoundTrack track in built.Tracks)
        {
            if (track.Value is { } handle && NestedTime(built, handle, track.Pulse) is { } time)
            {
                ApplyTrack(built, track, at, time);
            }
        }

        foreach (BoundWindow window in built.Windows)
        {
            if (window.Value is { } handle && NestedTime(built, handle, window.Pulse) is { } time)
            {
                built.Binding.Gate(window.Slot, window.Window.IsOpen(time));
            }
        }
    }

    /// <summary>Whether an element sits at or under a piece's root element.</summary>
    private static bool InSubtree(Engine_UiScreen screen, int element, int root) =>
        Engine_UiTimeline.InSubtree(screen, element, root);

    /// <summary>The live HUD, for the debug channel.</summary>
    public static Engine_HudView? Current { get; private set; }

    /// <summary>Debug lever: false parks the HUD walk entirely. See <see cref="_Process"/>.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// Where the HUD walk's time goes, in Stopwatch ticks, read back and zeroed by the `hud` op.
    /// </summary>
    /// <remarks>
    /// One counter per section of this walk; <see cref="Engine_UiCloneBinding"/> profiles the
    /// binding's own share separately.
    /// </remarks>
    public static long TicksValues;

    public static long TicksGates;

    public static long TicksApply;

    public static long TicksTexts;

    public static long TicksPip;

    public static long TicksPlace;

    public static long TicksTransform;

    public static long TicksDrive;

    public static long WalkedPieces;

    public static long Walks;

    public static void ResetWalkProfile()
    {
        TicksValues = 0;
        TicksGates = 0;
        TicksApply = 0;
        TicksTexts = 0;
        TicksPip = 0;
        TicksPlace = 0;
        TicksTransform = 0;
        TicksDrive = 0;
        WalkedPieces = 0;
        Walks = 0;
    }

    /// <summary>
    /// Every built piece whose id contains <paramref name="match"/>: where it is, how big, whether
    /// it is drawn, and what text it shows — the readout for "small and misaligned".
    /// </summary>
    public List<Dictionary<string, object?>> Describe(string match)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (Built built in _built)
        {
            if (match.Length != 0 &&
                !built.Piece.Id.Contains(match, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!GodotObject.IsInstanceValid(built.Node))
            {
                continue;
            }

            Aabb? bounds = null;
            foreach ((MeshInstance3D label, int _) in built.LiveTextNodes)
            {
                if (GodotObject.IsInstanceValid(label) && label.Mesh is not null && label.IsVisibleInTree())
                {
                    Aabb box = label.GlobalTransform * label.Mesh.GetAabb();
                    bounds = bounds is null ? box : bounds.Value.Merge(box);
                }
            }

            var texts = new List<string>();
            foreach ((int _, string text) in built.ShownTexts)
            {
                texts.Add(text);
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = built.Piece.Id,
                ["visible"] = built.Node.IsVisibleInTree(),
                ["position"] = new[] { built.Node.GlobalPosition.X, built.Node.GlobalPosition.Y, built.Node.GlobalPosition.Z },
                ["scale"] = new[] { built.Node.Scale.X, built.Node.Scale.Y },
                ["textBounds"] = bounds is { } b ? new[] { b.Position.X, b.Position.Y, b.Size.X, b.Size.Y } : null,
                ["texts"] = texts,
                ["children"] = built.Node.GetChildCount(),
            });
        }

        return rows;
    }

    public override void _Process(double delta)
    {
        // Player one's view is the one the debug channel reads.
        if (Seat != Engine_UiSeats.Seat.PlayerTwo)
        {
            Current = this;
        }

    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("hudview");

        // A bisect lever, like `render lights=` and `render entities=`: switching the walk off
        // separates its cost from the render queue behind it, which the phase timer cannot.
        if (!Enabled)
        {
            Visible = false;
            return;
        }

        // The seat's window is what every anchor resolves against; the node's own transform puts
        // that seat canvas on its half. The whole window writes nothing, as it never has.
        Engine_UiSeats.Frame frame = SeatFrame;
        if (!frame.IsWhole || _seated)
        {
            Transform = frame.IsWhole ? Transform3D.Identity : frame.Transform(0f);
            _seated = !frame.IsWhole;
        }

        if (_modPieces is not null)
        {
            _modPieces.Window = frame.Window;
        }

        DriveModMeters(frame);

        if (_built.Count == 0)
        {
            return;
        }

        if (ReadsValues)
        {
            Values.Advance((float)delta);
        }

        // On the guest's frame, or while anything is moving on Godot's clock (a gliding meter, a
        // running pulser): the guest publishes per HF tick, and the walk below crosses into
        // every piece's node.
        // A resized window re-places every piece even while the guest is not stepping.
        if (_refreshed && !Refii.Core.Engine_GuestFrame.Changed && !Values.Seeking &&
            Window == _placedWindow && Seat == _placedSeat && frame == _placedFrame &&
            (Seat == Engine_UiSeats.Seat.Whole ||
                (Suppressed == _placedSuppressed && ExpressionPlayers == _placedExpression)))
        {
            return;
        }

        // The guest's channels change only on its frames; between them the walk re-places the
        // pieces from the values gliding on Godot's clock (Engine_HudValues.Advance).
        bool read = !_refreshed || Refii.Core.Engine_GuestFrame.Changed;
        _placedWindow = Window;
        _placedSeat = Seat;
        _placedFrame = frame;
        _placedSuppressed = Suppressed;
        _placedExpression = ExpressionPlayers;
        _refreshed = true;
        ++Walks;
        long walkMark = System.Diagnostics.Stopwatch.GetTimestamp();
        if (read && ReadsValues)
        {
            Values.Read();
        }

        TicksValues += System.Diagnostics.Stopwatch.GetTimestamp() - walkMark;

        // Nothing before the first publish: an empty registry means "the hero has not spawned",
        // not "everything is at zero".
        Visible = Values.Live && !Suppressed;
        if (!Visible)
        {
            return;
        }

        Vector2 extent = frame.Extent;
        float scale = Engine_HudLayout.Scale(extent);

        // Counted over this pass; reset here rather than beside the report, which would zero them
        // before they are read.
        _hiddenByState = 0;
        _hiddenByValue = 0;
        _hiddenByDrive = 0;
        _why.Clear();
        _culprits.Clear();

        foreach (Built built in _built)
        {
            ++WalkedPieces;
            long mark = System.Diagnostics.Stopwatch.GetTimestamp();

            // A widget's stand-in in the editor: at its place, its frame inside the screen edge
            // its home is on.
            if (built.StandsIn)
            {
                Vector2 point = Engine_HudLayout.Place(built.Piece, extent);
                Vector2 half = (Engine_HudWidgets.FindByName(built.Piece.Art) is { } widget ? widget.Size : Vector2.Zero) *
                    built.ExtraScale * scale * 0.5f;
                Vector2 inside = new(-(built.Piece.Anchor.X - 0.5f) * 2f * half.X, (built.Piece.Anchor.Y - 0.5f) * 2f * half.Y);
                built.Node.Position = new Vector3(point.X + inside.X, point.Y + inside.Y, 0.4f);
                built.Node.Scale = new Vector3(scale, scale, 1f);
                built.Node.Visible = true;
                continue;
            }

            // Another seat's piece: player two's health in player one's half, or the reverse.
            if (!Admits(built.Rules))
            {
                built.Node.Visible = false;
                continue;
            }

            // A keyed piece is up and down as its player-two source is.
            int state = built.StateOrdinal;

            // THE RESOLVE THIS PIECE READS: the frame's, or — for a copy that states a slide of
            // its own (a mod meter in the game's own art, its fader raised for the copy alone) —
            // the copy's own resolve, made from the same inputs with that slide in place. The
            // original's ordinal is never touched: it resolves dark as the file authors it.
            Engine_UiElementResolve? stateResolve = ResolvedFor(built, read);

            // The slide toggle first, for a group: its authored `Off`/`TurnOff…` slide is the
            // guest's statement that the box is down, and the fold cannot say so on its own.
            // A GROUP IS DOWN WHEN ITS ROOT RESOLVES DARK, not when its slide is NAMED Off: the
            // timing meter's root carries the FlashExpressionMeter toggle, whose slide 1 is
            // 'Off' and only stops the flash — the meter's own visibility is the frame's toggle
            // beneath it. The name test hid the meter for every job. The fold and the played-
            // slide release make the root's opacity say what the slide meant.
            if (state >= 0 &&
                built.Piece.Id.StartsWith("group.", System.StringComparison.Ordinal) &&
                stateResolve is { } rootResolve && state < rootResolve.Opacity.Length &&
                rootResolve.Opacity[state] <= 0.001f)
            {
                built.Node.Visible = false;
                ++_hiddenByState;
                if (_explain)
                {
                    Note(built, $"element {state} resolves dark on slide " +
                        $"'{LiveSlideName(state)}'");
                }
                continue;
            }

            // The state gate first: the guest says whether this piece exists right now, and an
            // off piece takes no drive at all.
            // A copy on its own slides answers from its own resolve, by the frame's own formula
            // (drawn, and not faded out).
            bool off = built.SlideOverrides is { Count: > 0 }
                ? state >= 0 && (stateResolve is not { } own || state >= own.Drawn.Length ||
                    !own.Drawn[state] || own.Opacity[state] <= 0.01f)
                : state >= 0 && (ElementActive is not { } active || state >= active.Length || !active[state]);
            if (off)
            {
                built.Node.Visible = false;
                ++_hiddenByState;

                // The numbers that decided it, not just the count: the terms behind `active`
                // separate a parked HUD from a broken resolve.
                if (_explain)
                {
                    Note(built, Resolve is { } r &&
                            state < r.Drawn.Length
                        ? $"element {state} INACTIVE " +
                            $"(drawn={r.Drawn[state]} " +
                            $"opacity={r.Opacity[state]:0.00})" +
                            Culprit(state, r)
                        : $"element {state} INACTIVE (no resolve)");
                }
                continue;
            }

            // No second gate on a channel that is not read. The GUI registries are MeterName 1,
            // SlideFaderName 2, SlideFader2Name 3, PulserName 4, TextName 6, SlideToggleName 9,
            // and `walk_gui_registry` runs for 1 and 6 only, so toggle/fader/pulser names are
            // unreadable here. The guest applies those as slides on the element (`sub_822A9C60`
            // resolves the name to its bound elements, `sub_828421F8`/`sub_823FE848` push the
            // slide), whose commands carry the active and opacity the state gate above reads.

            built.Node.Visible = true;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksGates += now - mark;
            mark = now;

            // The piece's own parts follow the guest per element — which page of a widget is up,
            // what has been ghosted, and which picture each slot carries.
            if (stateResolve is { } resolved)
            {
                built.Binding.Apply(resolved, LiveTextures, LiveTransform);
            }

            PaintCloneIcon(built);

            RescaleClonedBars(built);

            now = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksApply += now - mark;
            mark = now;

            // By piece ID: the flash container for the same element is also Art 'ExpressionMeter',
            // and lighting the pip in that hollow clone put a white dot at the screen's centre.
            if (built.Piece.Id == "group.ExpressionMeter.ExpressionMeter")
            {
                DrivePip(built);
            }

            // Timed apart from the live texts below.
            now = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksPip += now - mark;
            mark = now;

            // The guest's own strings on the wheel's titles, applied only when they change: it
            // restates them every frame it holds the wheel.
            if (built.LiveTextNodes.Count != 0 && LiveTexts is { } texts && Theme is not null)
            {
                foreach ((MeshInstance3D label, int ordinal) in built.LiveTextNodes)
                {
                    int stated = built.StateOf(ordinal);
                    string live = stated >= 0 && stated < texts.Length
                        ? texts[stated] ?? string.Empty
                        : string.Empty;

                    // A LABEL THE GUEST NEVER WRITES KEEPS THE ONE THE FILE GAVE IT. Not a
                    // general rule — a field the guest deliberately CLEARS must go blank, and
                    // falling back everywhere would put authoring placeholders on screen as if
                    // they were data, which is worse than empty.
                    //
                    // Narrowed to the names the binary proves carry no text. `TargetedQueryButton`
                    // is hashed in exactly two places — its initialiser and one publish, and that
                    // publish is `sub_823FE848` at 0x823fd71c, a registry-9 BOOL for visibility.
                    // No text writer exists for it anywhere in the image, while `hud.bgf` binds
                    // the name to TWO text elements which author `GUI_DIALOG_MORE_INFO` and
                    // `GUI_SCREEN_GIFT_ACCEPT`. The guest shows those; we were blanking them.
                    if (live.Length == 0 &&
                        built.Piece.Id.Contains(".TargetedQueryButton.", System.StringComparison.Ordinal) &&
                        built.AuthoredTexts.TryGetValue(ordinal, out string? authoredText))
                    {
                        live = authoredText;
                    }
                    if (built.ShownTexts.TryGetValue(ordinal, out string? shown) && shown == live)
                    {
                        continue;
                    }

                    built.ShownTexts[ordinal] = live;
                    if (GodotObject.IsInstanceValid(label))
                    {
                        Theme.SetMeshText(label, live);
                    }
                }
            }

            now = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksTexts += now - mark;
            mark = now;

            Vector2 at = Engine_HudLayout.Place(built.Piece, extent);

            // The guest's own move of a piece root. A layout offset derives from the authored
            // canvas position, but the guest restates a root's position (quest text, the health
            // bar's drop). The clone binding applies stated transforms only beneath a root, so
            // the difference is added here.
            at += StatedDelta(built) * scale;

            // A meter copy stands by its art, not its element's origin (Built.ArtShift).
            if (built.ArtShift != Vector3.Zero)
            {
                Vector3 shift = built.Authored * built.ArtShift;
                at += new Vector2(shift.X, shift.Y) * scale * built.ExtraScale;
            }

            // `SetDPadCenter(true)` — the whole cluster under the mover shifts by the authored
            // delta; every piece cut from that subtree shifts with it. See BindCentreToggle.
            if (_centreMover >= 0 && Values.DpadCentered == true && Screen is { } doc &&
                built.Piece.ElementOrdinal >= 0 &&
                InSubtree(doc, built.Piece.ElementOrdinal, _centreMover))
            {
                at.X += _centreDelta * scale;
            }

            // The canvas factor multiplies the authored size, never replaces it. Composed from
            // the remembered basis rather than accumulated onto the live one, so the write stays
            // absolute across frames.
            now = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksPlace += now - mark;
            mark = now;

            built.Node.Transform = new Transform3D(
                built.Authored.Scaled(new Vector3(scale * built.ExtraScale, scale * built.ExtraScale, 1f)),
                built.Node.Transform.Origin);

            now = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksTransform += now - mark;
            mark = now;

            switch (built.Piece.Driven)
            {
                // The playhead, where the file authored one: a meter value scrubs the element
                // timeline (t = value x 10) and the piece's tracks say what moves. The left-edge
                // bar scaling below serves only meter pieces with no applicable tracks.
                case Engine_HudLayout.Drive.Bar when built.Tracks.Count > 0:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    ScrubTracks(built, at);
                    break;

                // The pulser loops the timeline: the guest advances the phase and publishes it
                // (registry 4); the tick is `phase × 1000`. No phase means it is not pulsing —
                // the art stays as authored rather than hiding.
                case Engine_HudLayout.Drive.Pulse:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    if (built.Tracks.Count > 0 && Values.Pulser(built.Value) is { } phase)
                    {
                        ScrubAt(built, at, Mathf.Clamp(phase, 0f, 1f) * 1000f);
                    }

                    break;

                case Engine_HudLayout.Drive.Bar:
                    DriveBar(built, at, scale);
                    break;

                case Engine_HudLayout.Drive.Text:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    DriveText(built);
                    break;

                default:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    if (built.Tracks.Count > 0)
                    {
                        ScrubNested(built, at);
                    }

                    break;
            }

            TicksDrive += System.Diagnostics.Stopwatch.GetTimestamp() - mark;

            // The drive hides too: `ScrubTracks` and `DriveBar` return early on a meter the guest
            // has never published, after both gates above have passed.
            if (!built.Node.Visible)
            {
                ++_hiddenByDrive;
                if (_explain)
                {
                    Note(built, $"the drive found no value for '{built.Piece.Value}'");
                }
            }
        }

        // Which channels the guest has published, re-reported when the set changes — keyed on the
        // change so a HUD that is merely waiting does not read as broken.
        List<Engine_HudValues.Handle> wanted = _wanted;
        wanted.Clear();
        foreach (Built built in _built)
        {
            if (built.Piece.Value.Length != 0)
            {
                wanted.Add(built.Value);
            }

            // AND THE CHANNELS NESTED INSIDE A GROUP. A group piece is built with an EMPTY
            // value — the names it drives belong to the meters inside it — so every nested
            // channel was invisible to this report. That covers the hold rings on the top box
            // (`RightTopButton`, `LeftTopButton`) and the box's own extent meters, which is
            // exactly the set you need when a hold prompt will not fill: without them, a guest
            // that never publishes and a guest publishing zero look identical, because the
            // meter read returns 0 rather than nothing for a name the presentation registered.
            foreach (BoundTrack track in built.Tracks)
            {
                if (track.Value is { } nested && !wanted.Contains(nested))
                {
                    wanted.Add(nested);
                }
            }
        }

        // Key first, string only on a change: both halves of this line allocate and it rarely
        // changes. See Engine_HudValues.DescribeKey.
        long publishedKey = Values.DescribeKey(wanted);
        if (publishedKey != _publishedKey)
        {
            _publishedKey = publishedKey;
            GD.Print($"{Layout.Describe(extent)}; {Values.Describe(wanted)}");
        }

        DrawModernPanels();

        // Who is on screen, named by id on change; the names are gathered only when the key
        // moves.
        long visibleKey = 17L;
        int visibleCount = 0;
        foreach (Built built in _built)
        {
            if (built.Node.Visible)
            {
                ++visibleCount;
                visibleKey = (visibleKey * 1000003L) ^ built.Piece.Id.GetHashCode();
            }
        }

        if (visibleKey != _visibleKey)
        {
            _visibleKey = visibleKey;
            var visible = new List<string>(visibleCount);
            foreach (Built built in _built)
            {
                if (built.Node.Visible)
                {
                    visible.Add(built.Piece.Id);
                }
            }

            GD.Print($"{Tag} visible ({visible.Count} of {_built.Count}): {string.Join("  ", visible)}");

            // The reasons are gathered and printed on the next pass only, so they are not
            // formatted every frame.
            _explain = true;
        }
        else if (_explain)
        {
            _explain = false;

            // Why the rest are not: the state gate is the guest's answer, the value gate is
            // host-side, and the count alone cannot tell a parked element from a broken join.
            GD.Print($"{Tag} hidden — {_hiddenByState} by the guest's element state, " +
                $"{_hiddenByValue} by an unpublished channel before the drive (should be 0), " +
                $"{_hiddenByDrive} by the drive itself; " +
                $"{(_why.Count < WhyCap ? "all" : $"first {WhyCap}")} {_why.Count}: " +
                string.Join("  ", _why));

            // The same findings grouped by culprit element rather than per piece.
            if (_culprits.Count > 0)
            {
                var parts = new List<string>();
                foreach ((string where, int count) in _culprits)
                {
                    parts.Add($"{where} x{count}");
                }

                parts.Sort();
                GD.Print($"[hud] dark at: {string.Join("  ", parts)}");
            }
        }
    }

    /// <summary>The pass after the visible set changed gathers the reasons; every other pass skips them.</summary>
    private long _publishedKey = long.MinValue;
    private long _visibleKey = long.MinValue;
    private readonly HashSet<string> _panelsUsed = new();

    private bool _explain;

    private int _groupSpanFrame;

    private bool _refreshed;

    private Vector2 _placedWindow;

    private Engine_UiSeats.Seat _placedSeat;

    /// <summary>The seat's frame the pieces were last placed in: a split that changes from side by
    /// side to stacked moves the half without changing the seat or the window.</summary>
    private Engine_UiSeats.Frame _placedFrame;

    private bool _placedSuppressed;

    private int _placedExpression = 1;

    /// <summary>The log tag: player two's view says so, or its lines read as player one's.</summary>
    private string Tag => Seat == Engine_UiSeats.Seat.PlayerTwo ? "[hud p2]" : "[hud]";

    /// <summary>Held hidden by the owner (the full cutscene replaces the whole GUI) — the walk
    /// restates <see cref="Node3D.Visible"/> every guest frame, so a bare hide does not hold.</summary>
    public bool Suppressed { get; set; }

    private readonly List<Engine_HudValues.Handle> _wanted = new();

    /// <summary>Why the hidden pieces are hidden, counted over the last pass.</summary>
    private int _hiddenByState;

    private int _hiddenByValue;

    private int _hiddenByDrive;

    /// <summary>A sample of hidden pieces with the reason, capped.</summary>
    private readonly List<string> _why = new();

    /// <summary>How many dark pieces each zeroing element accounts for, over the last pass;
    /// uncapped, unlike <see cref="_why"/>.</summary>
    private readonly Dictionary<string, int> _culprits = new();

    private void Note(Built built, string reason)
    {
        if (_why.Count < WhyCap)
        {
            _why.Add($"{built.Piece.Id}: {reason}");
        }
    }

    /// <summary>How many hidden pieces the report names before it stops.</summary>
    private const int WhyCap = 24;

    /// <summary>
    /// The element that put this piece in the dark, and how. Opacity accumulates down the tree,
    /// so the piece's own number rarely decided anything: this names the first ancestor whose own
    /// term is zero, and which channel set it — a guest-stated zero is followed, an authored zero
    /// is the scene's parked state.
    /// </summary>
    private string Culprit(int ordinal, Engine_UiElementResolve resolve)
    {
        if (Screen is not { } screen || ordinal < 0 || ordinal >= screen.Nodes.Length ||
            resolve.Opacity[ordinal] > 0.01f)
        {
            return string.Empty;
        }

        for (UiNode? at = screen.Nodes[ordinal]; at is not null; at = at.Parent)
        {
            int up = at.Ordinal;
            if (up >= resolve.Own.Length || resolve.Own[up] > 0.01f)
            {
                continue;
            }

            string how = resolve.Stated[up] ? "the guest STATED it" : "AUTHORED/slid";
            string where = $"'{at.Name}'#{up} ({how})";
            _culprits[where] = _culprits.TryGetValue(where, out int seen) ? seen + 1 : 1;
            return $" — dark at {where}";
        }

        return " — dark with no element at zero (a parent chain we did not resolve)";
    }


    /// <summary>
    /// Scrubs a piece's authored tracks to its meter's playhead. An unpublished channel hides the
    /// piece; a published value maps to t = value x 10 and each track evaluates the engine's
    /// bezier at that time. Root-element position tracks apply as deltas from the authored parked
    /// value (the layout owns the anchor); child tracks apply local values verbatim. Opacity is
    /// authored in percent and scaled by 0.01 at apply.
    /// </summary>
    private void ScrubTracks(Built built, Vector2 at)
    {
        if (Values.Meter(built.Value) is not { } value)
        {
            built.Node.Visible = false;
            return;
        }

        ScrubAt(built, at, Mathf.Clamp(value, 0f, 100f) * 10f);
    }

    /// <summary>Writes every bound track and window of a piece at one tick of its timeline.</summary>
    private static void ScrubAt(Built built, Vector2 at, float t)
    {
        foreach (BoundTrack track in built.Tracks)
        {
            if (track.Value is null)
            {
                ApplyTrack(built, track, at, t);
            }
        }

        GateWindows(built, null, t);
    }

    /// <summary>Writes one bound track at one tick.</summary>
    private static void ApplyTrack(Built built, BoundTrack track, Vector2 at, float t)
    {
        if (!GodotObject.IsInstanceValid(track.Node))
        {
            return;
        }

        float evaluated = track.Animation.Evaluate(t);

        // The piece's own root is placed by the layout: its position track moves it relative to
        // where the layout put it.
        if (ReferenceEquals(track.Node, built.Node) &&
            track.Target is Engine_UiTimeline.Target.PositionX or Engine_UiTimeline.Target.PositionY)
        {
            track.Node.Position = track.Target == Engine_UiTimeline.Target.PositionX
                ? track.Node.Position with { X = at.X + (evaluated - track.Base) }
                : track.Node.Position with { Y = at.Y + (evaluated - track.Base) };
            return;
        }

        // A COPY ON ITS OWN SLIDES has no guest-stated transform re-anchoring its bar every frame
        // (Engine_UiCloneBinding.ApplyTransform puts an element's origin at position − basis·pivot
        // from what the guest states), so its scrubbed scale is anchored at the authored pivot
        // here: every fill in hud.bgf pivots at its left edge (pivot.x −50), and growing about the
        // origin instead walked the generic meter's fill 124 units out of its frame.
        if (built.SlideOverrides is not null &&
            track.Target is Engine_UiTimeline.Target.ScaleX or Engine_UiTimeline.Target.ScaleY &&
            built.Binding.PivotOf(track.Node) is { } pivot && pivot != Vector3.Zero)
        {
            Basis before = track.Node.Basis;
            Engine_UiTimeline.Write(track.Node, track.Target, evaluated);
            track.Node.Position += (before * pivot) - (track.Node.Basis * pivot);
            return;
        }

        Engine_UiTimeline.Write(track.Node, track.Target, evaluated);
    }

    /// <summary>
    /// Scales a bar by its meter, keeping the left edge where the layout put it: a quad is
    /// centred on its origin, so the node shifts by half the width removed.
    /// </summary>
    private void DriveBar(Built built, Vector2 at, float scale)
    {
        // Unpublished is not empty: a never-written meter hides the bar, a meter written as zero
        // draws an empty one.
        if (Values.Fraction(built.Value) is not { } fraction)
        {
            built.Node.Visible = false;
            return;
        }

        built.Node.Visible = true;
        built.Node.Scale = new Vector3(scale * fraction, scale, 1f);

        float full = built.Width * scale;
        built.Node.Position = new Vector3(
            at.X - (full * (1f - fraction) * 0.5f), at.Y, built.Piece.Depth);
    }

    /// <summary>Lays a text piece out in the shipped font, only when the string changes.</summary>
    /// <summary>
    /// How far the guest has moved a piece's element from its authored canvas position, in
    /// canvas units — zero when it has stated nothing. Composed through the parent chain
    /// exactly as the authored position is, so a stated local position lands where the file's
    /// hierarchy puts it.
    /// </summary>
    private Vector2 StatedDelta(Built built)
    {
        int ordinal = built.StateOrdinal;
        if (Screen is not { } doc || ordinal < 0 || ordinal >= doc.Nodes.Length ||
            LiveTransform is null || ordinal >= LiveTransform.Count ||
            LiveTransform[ordinal] is not { } stated || stated.Length < 2 ||
            (stated[0] is null && stated[1] is null))
        {
            return Vector2.Zero;
        }

        UiNode node = doc.Nodes[ordinal];

        // The adapter's write is not a move. `widescreenadapter.lua` writes the wide position
        // onto the element itself and the layout already placed the piece there
        // (`CanvasPositionWide`), so the baseline is the wide position where the wide layout
        // overrides this element; measuring from the 4:3 position would count it twice.
        var baseline = new Vector2(node.PositionX, node.PositionY);
        if (Layout.Wide is { } wide && wide.TryGetValue(node.Ordinal, out Engine_UiWidescreen.Override over))
        {
            baseline = new Vector2(over.X ?? baseline.X, over.Y ?? baseline.Y);
        }

        var at = new Vector2(stated[0] ?? baseline.X, stated[1] ?? baseline.Y);
        for (UiNode? up = node.Parent; up is not null; up = up.Parent)
        {
            at = new Vector2(up.PositionX + (up.ScaleX * at.X), up.PositionY + (up.ScaleY * at.Y));
            baseline = new Vector2(up.PositionX + (up.ScaleX * baseline.X), up.PositionY + (up.ScaleY * baseline.Y));
        }

        return at - baseline;
    }

    private void DriveText(Built built)
    {
        if (built.Node is not MeshInstance3D label || Theme is null)
        {
            return;
        }

        string live = Values.Text(built.Value) ?? string.Empty;
        if (live == built.Shown)
        {
            return;
        }

        built.Shown = live;
        if (live.Length == 0)
        {
            label.Mesh = null;
            label.MaterialOverride = null;
            return;
        }

        // The `.fac` record states the alignment and colour, and the guest positions the block's
        // left edge (`x = P.x - (px + 0.5) * w`).
        UiText? record = Screen is { } doc && built.Piece.ElementOrdinal >= 0 &&
            built.Piece.ElementOrdinal < doc.Nodes.Length
                ? doc.Nodes[built.Piece.ElementOrdinal].Text
                : null;
        var colour = record is not null
            ? new Color(record.R, record.G, record.B, 1f)
            : new Color(1f, 224f / 255f, 128f / 255f, 1f);
        // The record's own wrap box. A subtitle arrives ALREADY WRAPPED: the subtitler
        // (`sub_827E8560`) wraps each line at the Subtitles layout box (`sub_82803138` at
        // 0x827E8814), breaks it with U+000A and publishes three lines a page, and places the
        // measured block's left edge. Its record says no wrap; re-wrapping at the record's
        // 240-unit box stacked five lines down the left of the screen.
        Theme.Paint(
            label, live, record?.Size is > 0 and var size ? size : TextSize, colour,
            horizontalAlign: record?.HorzAlign ?? 0, verticalAlign: record?.VertAlign ?? 1,
            boxWidth: record?.BoxWidth ?? 0f, boxHeight: record?.BoxHeight ?? 0f,
            wrapMode: record?.WrapMode ?? 0);
    }

    /// <summary>Point size for HUD text, at the layout's reference height.</summary>
    public const int TextSize = 18;

    /// <summary>
    /// A HUD piece sits in the 3D scene ~150 m in front of the camera, inside the sun's far
    /// cascade; left casting, its alpha-tested quads go into the shadow map every frame. Screen
    /// elements throw no shadow.
    /// </summary>
    private static void NoShadow(Node node)
    {
        if (node is GeometryInstance3D geometry)
        {
            geometry.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }

        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            Node child = node.GetChild(i);
            NoShadow(child);
        }
    }
}
