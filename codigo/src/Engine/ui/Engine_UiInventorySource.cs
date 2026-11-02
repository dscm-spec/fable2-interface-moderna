using System.Collections.Generic;
using Refii.Core;
using Refii.Equipment;
using Refii.Gdb;

namespace Refii.Ui;

/// <summary>
/// THE HERO'S INVENTORY, READ FROM THE DATA AREAS — the first menu source that is not a copy of
/// something the guest's UI did.
///
/// Every other list source watches the guest populate its own Gameface menu and mirrors the
/// result. This one asks the simulation what the hero is carrying and asks the shipped data what
/// each thing is called, which is the whole point: the rows exist whether or not a Gameface
/// screen was ever built, so the list can be drawn by the editor, by a mod, or by a screen the
/// original never had.
///
/// Two inputs, both already decoded and neither of them the UI:
///
/// - <see cref="Refii.Host.HostBridge.GetHeroInventory"/> — one record per stack, carrying the
///   GDB key, how many, the category and the new-item marker. It has existed unread.
/// - `globals.gdb` — the record's `NameTag` and `DescriptionTag` (Babel tags), its `IconGraphic`
///   and its `Rating`, most of them INHERITED: a clothing item states only its two tags locally
///   and takes the icon, rating and category from its parent chain up to `InventoryItemComponent`.
///   `GdbMulti.TryFindInherited` is that walk.
///
/// The rows are not committable. A row here has no guest identity because it was never spawned
/// into a guest menu, and it does not need one — activating it is a host action, not a
/// `PushButtonEvent` for a screen class to interpret.
/// </summary>
public static class Engine_UiInventorySource
{
	/// <summary>The source name a template asks for: `inventory` for everything, `inventory:N`
	/// for one category.</summary>
	public const string SourceName = "inventory";

	/// <summary>
	/// The item CATEGORY enum, 1..26, as the guest's own switch names it — `sub_826CEB60`, whose
	/// jump table at 0x826CEBE8 is indexed by `Category - 1` and whose arms write the group name
	/// the row is filed under. The same table drives `sub_82430B58`.
	///
	/// The NAMES are not in the executable's strings as `CategoryWeapons` and friends — those
	/// live only in `globals.gdb`, where each is a field whose value is the integer. The binary
	/// holds the integer→display-name mapping, and the two agree everywhere they overlap
	/// (Weapons 1, Misc/Tools 2, Gifts 3, Food 5, Potions 6, Dyes 8, Books 16, Dog 24).
	///
	/// Corroborated against a real run: the item screen spawns its nine category rows in the
	/// order KeyItems, Books, Food and Drink, Potions, Gifts, Trophies, Augmentations, Dyes,
	/// Furniture (`sub_829B1578`), which is exactly what the screen shows.
	/// </summary>
	public static class Category
	{
		public const int Weapons = 1;
		public const int KeyItems = 2;
		public const int Gifts = 3;
		public const int Trophies = 4;
		public const int Food = 5;
		public const int Potions = 6;
		public const int Augmentations = 7;
		public const int Dyes = 8;

		/// <summary>`sub_829B2168` asks `sub_829B1FC0` for 9, 10 and 11 and names the rows Hair, Beard, Mustache.</summary>
		public const int Hair = 9;
		public const int Beard = 10;
		public const int Moustache = 11;
		public const int Furniture = 15;
		public const int Books = 16;
		public const int Hats = 17;
		public const int Coats = 18;
		public const int Shirts = 19;
		public const int Gloves = 20;
		public const int Pants = 21;
		public const int Shoes = 22;
		public const int Accessories = 23;
		public const int Dog = 24;
		public const int Suits = 25;

		/// <summary>
		/// Every integer the guest names `KeyItems`. 2 is the category proper; 9..14 and 26 are
		/// folded onto it by `sub_826CEB60`'s jump table, which is why hairstyles and makeup
		/// appear under KeyItems on the real screen — that is the shipped grouping, not a defect.
		/// </summary>
		public static readonly int[] KeyItemsFold = { 2, 9, 10, 11, 12, 13, 14, 26 };

		/// <summary>
		/// 14 and 24 name a group but the guest spawns no header row for them
		/// (`sub_82430B58` returns 0 — table slots 0x82430BB8 and 0x82430BE0).
		/// </summary>
		public static bool HasHeaderRow(int category) => category != 14 && category != 24;

		/// <summary>
		/// The Babel tag for a category's header row — `GUI_SCREEN_INVENTORY_CATEGORIES_*`, all
		/// of which resolve in the shipped book. Empty where the guest spawns no header (14, 24)
		/// or names no group.
		/// </summary>
		public static string TagOf(int category) => category switch
		{
			Weapons => Tag + "WEAPONS",
			2 or 9 or 10 or 11 or 12 or 13 or 26 => Tag + "TOOLS",
			Gifts => Tag + "GIFTS",
			Trophies => Tag + "TROPHIES",
			Food => Tag + "FOOD",
			Potions => Tag + "POTIONS",
			Augmentations => Tag + "AUGMENTATIONS",
			Dyes => Tag + "DYES",
			Furniture => Tag + "FURNITURE",
			Books => Tag + "BOOKS",
			Hats => Tag + "HATS",
			Coats => Tag + "COATS",
			Shirts => Tag + "SHIRTS",
			Gloves => Tag + "GLOVES",
			Pants => Tag + "PANTS",
			Shoes => Tag + "SHOES",
			Accessories => Tag + "MASKS",
			Suits => Tag + "SUITS",
			_ => string.Empty,
		};

		private const string Tag = "GUI_SCREEN_INVENTORY_CATEGORIES_";

		/// <summary>The group name a category files an item under, or empty for none.</summary>
		public static string NameOf(int category) => category switch
		{
			Weapons => "Weapons",
			2 or 9 or 10 or 11 or 12 or 13 or 14 or 26 => "KeyItems",
			Gifts => "Gifts",
			Trophies => "Trophies",
			Food => "Food and Drink",
			Potions => "Potions",
			Augmentations => "Augmentations",
			Dyes => "Dyes",
			Furniture => "Furniture",
			Books => "Books",
			Hats => "Hats",
			Coats => "Coats",
			Shirts => "Shirts",
			Gloves => "Gloves",
			Pants => "Pants",
			Shoes => "Shoes",
			Accessories or Dog => "Accessories",
			Suits => "Suits",
			_ => string.Empty,
		};
	}

	/// <summary>`globals.gdb`, for the item records. Set once the banks are readable.</summary>
	public static GdbMulti? Globals { get; set; }

	/// <summary>The string book, for the item name tags.</summary>
	public static Refii.Text.Engine_BabelDatabase? Strings { get; set; }

	/// <summary>What the last read produced, for the report.</summary>
	public static string Report { get; private set; } = "not read yet";

	private static readonly uint NameTagField = Engine_GameHash.Fnv1("NameTag");
	private static readonly uint RatingField = Engine_GameHash.Fnv1("Rating");
	private static readonly uint IconField = Engine_GameHash.Fnv1("IconGraphic");
	private static readonly uint VisibleField = Engine_GameHash.Fnv1("VisibleInGUI");
	private static readonly uint UsableField = Engine_GameHash.Fnv1("UsableFromInventory");
	private static readonly uint InventoryComponentField =
		Engine_GameHash.Fnv1("InventoryItemComponent");
	private static readonly uint GiftComponentField = Engine_GameHash.Fnv1("GiftComponent");
	private static readonly uint CanBeGiftField = Engine_GameHash.Fnv1("CanBeGift");

	private static readonly List<Engine_UiMenuSources.Row> None = new();

	/// <summary>
	/// The hero's items, or one category of them. <paramref name="argument"/> is the category
	/// integer; anything that is not a number takes every category.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		List<Refii.Host.HostBridge.InventoryRecord> records =
			Refii.Host.HostBridge.GetHeroInventory();

		// WHAT THE HERO IS ALREADY WEARING. `sub_8242FB28` drops a clothing row when that item is
		// equipped AND the stack holds only one — a second copy stays offered, because there is
		// still one to put on. Read once per request rather than per row.
		var worn = new HashSet<uint>();
		var wornCategories = new HashSet<int>();
		foreach ((uint recordId, int category) in Refii.Host.HostBridge.GetWornItems())
		{
			worn.Add(recordId);
			wornCategories.Add(category);
		}
		if (records.Count == 0)
		{
			Report = "the hero states no inventory";
			Say(argument, Report);
			return None;
		}

		// `inventory:wear:17,18` — an optional OPERATION, then a comma list of categories. The
		// list is a list because a displayed group is not always one category: the guest folds
		// 2 and 9..14 and 26 onto `KeyItems`. The operation is here rather than on the template
		// entry because it needs the row's own record hash, which only this knows.
		string operation = SplitOperation(ref argument);
		HashSet<int>? wanted = ParseCategories(argument);

		if (operation == DyeOperation)
		{
			return DyeTargets(argument, wanted, records);
		}

		// GROUPED ONLY WHEN THE REQUEST SPANS MORE THAN ONE GROUP. A screen that authors its own
		// category rows asks for one group each — `KeyItems` is eight category integers but a
		// single group — and must not get a second header nested inside the row it already has.
		// A screen whose categories depend on what the player is carrying asks for all of them,
		// and there the headers ARE the list, created per group as rows are seen. That is the
		// guest's own arrangement: `sub_82430468` walks its 27 slots and emits a header only
		// where the count is non-zero.
		bool grouped = GroupsIn(wanted) > 1;
		var rows = new List<Engine_UiMenuSources.Row>(records.Count);
		int unnamed = 0;
		int hidden = 0;
		int ungiftable = 0;
		int equipped = 0;

		// WHAT THE HERO CARRIES, by record — an outfit is listed only for its pieces.
		var carried = new HashSet<uint>();
		foreach (Refii.Host.HostBridge.InventoryRecord record in records)
		{
			carried.Add(record.RecordId);
		}

		var outfits = new HashSet<uint>();
		int unwearable = 0;

		// THE SLOT'S `None` ROW, FIRST — the guest spawns one per clothing category immediately
		// after the header (`sub_829B3340` for 17..23, `sub_829B2D58` for Suits), and answers a
		// press on it by taking that slot's garment off.
		AddNoneRows(operation, wanted, wornCategories, grouped, rows);

		foreach (Refii.Host.HostBridge.InventoryRecord record in records)
		{
			if (wanted is not null && !wanted.Contains(record.Category))
			{
				continue;
			}

			if (!Listable(record, wanted is not null && wanted.Contains(record.Category)))
			{
				++hidden;
				continue;
			}

			// AN OUTFIT IS LISTED FOR ITS PIECES. `sub_829B2D58` spawns a suit the hero carries
			// only when `sub_829B2AE8` finds at least one of its `SuitParts` in the inventory by
			// record (`sub_824F1C90`) — an outfit with nothing to put on is not offered. One row
			// per outfit: a second copy of the same suit would dress the hero identically.
			string rowOperation = operation;
			if (record.Category == Category.Suits && operation == WearOperation)
			{
				if (!outfits.Add(record.RecordId))
				{
					++hidden;
					continue;
				}

				bool owned = false;
				foreach (uint piece in Engine_UiItemStats.SuitPieces(record.RecordId))
				{
					if (carried.Contains(piece))
					{
						owned = true;
						break;
					}
				}

				if (!owned)
				{
					++unwearable;
					continue;
				}

				rowOperation = OutfitOperation;
			}

			// A GIFT LIST IS THE INVENTORY NARROWED BY `sub_8242FB28`, and by nothing else — see
			// Listable for why the two exclusions below are not applied to every screen.
			if (operation == "gift")
			{
				// Case 0x19: an outfit is never a gift.
				if (record.Category == Category.Suits)
				{
					++hidden;
					continue;
				}

				// Cases 1 and 0x11..0x16: on, and the last one — nothing spare to give away.
				if (WearableCategory(record.Category) &&
					record.Count <= 1 && worn.Contains(record.RecordId))
				{
					++equipped;
					continue;
				}

				if (!CanBeGift(record.RecordId))
				{
					++ungiftable;
					continue;
				}
			}

			string tag = NameTagOf(record);
			string label = LabelOf(record, tag);
			if (record.Category is >= 17 and <= 23 && worn.Contains(record.RecordId))
			{
				label = EquippedLabel(label);
			}

			if (tag.Length == 0)
			{
				++unnamed;
			}

			rows.Add(new Engine_UiMenuSources.Row(
				// THE ITEM'S `NameTag`, WHICH IS THE GUEST'S OWN ROW NAME. `sub_826CE8D0` passes
				// exactly this string as `String1` (read at 0x8276457C), so a row we built from
				// the data areas and a row the guest spawned for the same item carry the same
				// identity — which is what lets the label join find it and what lets a commit
				// borrow the guest's `(EventType, id)` until host actions exist for these.
				//
				// Falls back to the GDB key so an item with no tag is still distinct.
				Name: tag.Length != 0 ? tag : $"Item:{record.RecordId:X8}",
				Label: label,
				Icon: IconOf(record),
				EventType: 0,
				Id: record.RecordId,
				Rating: RatingOf(record),

				// Never committable — see the class note.
				Committable: false,
				Ghosted: false,

				// THE ROW'S OWN ACTION, performed on the simulation without the guest's menus:
				// `Inventory:<op>:<record hash>` reaches the guest's own script-command entry
				// points (`AddAndReplaceItemOfRecordID`, `Player:GiveGift`) through the tick
				// pump. Empty when the screen states no operation, and then the row falls back
				// to borrowing the guest's identity.
				Command: Acts(rowOperation, record)
					? $"{InventoryAction}{rowOperation}:{record.RecordId}"
					: null,
				Group: grouped ? Category.NameOf(record.Category) : string.Empty,
				GroupTag: grouped ? Category.TagOf(record.Category) : string.Empty,
				IsHeader: false,

				// A worn clothing piece is emphasised: `sub_826D0890` states `Float1 = 1` for a
				// category 17..23 item `sub_824C4258` finds on component 15, then runs
				// `g_Menu:SetItemEmphasisByIndex()` (0x826D0BE4). Matched by record, the guest by
				// instance, so a second copy of a worn record shares the tint.
				Emphasised: record.Category is >= 17 and <= 23 && worn.Contains(record.RecordId),

				// An inventory record IS what the header counts, and the new-item marker follows
				// `*(item + 20) == 0` — the export states that byte as `Viewed`.
				Counted: true,
				Unviewed: !record.Viewed));
		}

		Report = $"{rows.Count} row(s) from {records.Count} inventory record(s)" +
			(wanted is null ? string.Empty : $" in categor(y/ies) {argument}") +
			(hidden == 0 ? string.Empty : $", {hidden} not listable") +
			(ungiftable == 0 ? string.Empty : $", {ungiftable} not giftable") +
			(equipped == 0 ? string.Empty : $", {equipped} already worn") +
			(unwearable == 0 ? string.Empty : $", {unwearable} outfit(s) with no piece carried") +
			(unnamed == 0 ? string.Empty : $", {unnamed} with no NameTag");

		// Change-keyed per request, because this is asked once per category per rebuild and a
		// line each would drown the log. An empty answer is the one that matters: it is the
		// difference between "you are carrying no potions" and "the read is broken", and the
		// counts here separate them.
		Say(argument, Report);
		return rows;
	}

	/// <summary>
	/// U10: the emphasised (purple) row is what the hero holds or wears, but the tint alone does
	/// not say so — the row also reads "(Equipado)". <c>refii_equipped_tag_off.flag</c> beside
	/// the exe drops the suffix (the tint stays, as shipped).
	/// </summary>
	private static string EquippedLabel(string label)
	{
		if (_equippedTag < 0)
		{
			bool off = false;
			try
			{
				string dir = System.IO.Path.GetDirectoryName(Godot.OS.GetExecutablePath()) ?? "";
				off = System.IO.File.Exists(System.IO.Path.Combine(dir, "refii_equipped_tag_off.flag"));
				Godot.GD.Print($"[ui-u10] equipped suffix: {(off ? "off (refii_equipped_tag_off.flag)" : "on")}");
			}
			catch (System.Exception)
			{
				off = false;
			}

			_equippedTag = off ? 0 : 1;
		}

		string suffix = Engine_UiLocale.IsPortuguese ? EquippedSuffix : EquippedSuffixEnglish;
		return _equippedTag == 1 && !label.EndsWith(suffix, System.StringComparison.Ordinal)
			? label + suffix
			: label;
	}

	private const string EquippedSuffix = " (Equipado)";
	private const string EquippedSuffixEnglish = " (Equipped)";
	private static int _equippedTag = -1;

	private static readonly Dictionary<string, string> Said =
		new(System.StringComparer.Ordinal);

	private static void Say(string argument, string report)
	{
		if (Said.TryGetValue(argument, out string? was) && was == report)
		{
			return;
		}

		Said[argument] = report;
		Godot.GD.Print($"[inventory-source] '{argument}': {report}");
	}

	/// <summary>
	/// Whether this row performs the screen's operation at all.
	///
	/// `use` is gated on the item's own `UsableFromInventory`, which is the guest's own test
	/// (`sub_827649C8`, checked at 0x826D19B8 before it will dispatch a use). A trophy and a book
	/// sit in the item list but are not consumable, and a row with no command falls back to
	/// borrowing the guest's identity rather than performing an operation the data forbids.
	///
	/// `wear` and `gift` are not gated here: wear is only ever offered on the clothing
	/// categories, and gift has its own predicate on a component this cannot yet read.
	/// </summary>
	private const string WearOperation = "wear";

	/// <summary>
	/// `Inventory:outfit:<suit>` — the pump runs `sub_826CBF10`, which puts on the hero's own
	/// pieces of the suit and never the suit item itself (`sub_826D3A10`, event 89).
	/// </summary>
	private const string OutfitOperation = "outfit";

	private static bool Acts(string operation, Refii.Host.HostBridge.InventoryRecord record)
	{
		if (operation.Length == 0)
		{
			return false;
		}

		if (operation != "use")
		{
			return true;
		}

		return TryItemField(record.RecordId, UsableField, (int)GdbFieldType.Bool, out uint usable) &&
			usable != 0;
	}

	/// <summary>
	/// A COMPONENT of an item, by the field the entity names it with.
	///
	/// `GetHeroInventory` returns `item + 0x04`, and that is the ENTITY record — it carries
	/// `InventoryItemComponent`, `AppearanceModifierComponent` and so on as reference fields, and
	/// carries none of the item's own properties. Every property a list needs lives on one of
	/// those components: `NameTag`, `IconGraphic`, `Rating` and `UsableFromInventory` on the
	/// InventoryItemComponent, `CanBeGift` on the GiftComponent.
	///
	/// Reading them off the entity returns nothing, which is exactly what a screen full of
	/// `0x959C9372 x2` looks like. Verified against the shipped file: entity `0x959C9372` names
	/// InventoryItemComponent `33882401`, and THAT record holds
	/// `NameTag = INV_ITEM_CLOTHES_BANDIT_SUIT_NAME`.
	///
	/// The hop is one field lookup; the INHERITANCE walk happens on the far side, because a
	/// clothing item states only its two tags locally and takes icon and rating from its parents
	/// up to `InventoryItemComponent`.
	/// </summary>
	private static bool TryComponent(uint entity, uint field, out GdbCursor cursor)
	{
		cursor = default;
		return Globals is { } globals &&
			globals.TryLookup(entity, out GdbCursor owner) &&
			globals.TryFindInherited(owner, field, (int)GdbFieldType.Reference,
				out _, out uint component, out _) &&
			component != 0u &&
			globals.TryLookup(component, out cursor);
	}

	/// <summary>One inherited field off an item's InventoryItemComponent.</summary>
	/// <summary>
	/// A whole number off an item, WHICHEVER of the two numeric types it was authored as.
	///
	/// The shipped database mixes them and the two reads here had it backwards BOTH ways:
	/// `Rating` is `enum` on all 513 records that state it and was read as `int`, while
	/// `WeaponType` is `int` on all 128 and was read as `enum`. A wrong type code returns
	/// nothing and says nothing, so both simply behaved as though the field were absent.
	/// </summary>
	private static bool TryItemNumber(uint entity, uint field, out uint value) =>
		TryItemField(entity, field, (int)GdbFieldType.Int, out value) ||
		TryItemField(entity, field, (int)GdbFieldType.Enum, out value);

	private static bool TryItemField(uint entity, uint field, int type, out uint value)
	{
		value = 0u;
		return TryComponent(entity, InventoryComponentField, out GdbCursor item) &&
			Globals!.TryFindInherited(item, field, type, out _, out value, out _);
	}

	/// <summary>A string field's text, through the GDB's own dictionary.</summary>
	private static string ItemString(uint entity, uint field) =>
		TryItemField(entity, field, (int)GdbFieldType.String, out uint hash) &&
		Globals!.TryLookup(entity, out GdbCursor any) &&
		any.View.Names.TryGetValue(hash, out string? text)
			? text
			: string.Empty;

	/// <summary>
	/// The guest's gift predicate — `CanBeGift` on the item's `GiftComponent`, which
	/// `sub_8242FB28` reads at 0x82430148 and rejects outright when false or absent.
	///
	/// `IsRomantic` (0x82430194) is deliberately NOT applied, and the reason is not the one this
	/// comment used to give. It is an AGE GATE, not a partner test: the guest rejects a romantic
	/// gift only when `sub_821C2A18(targetAge) &lt; 2` — that function is registered as the Lua
	/// `GetAgeGroup` and returns 0 below 1.0, 1 below 18.0, 2 below 50.0, 3 above. So the shipped
	/// rule is "do not offer romantic gifts to a child", and it costs nothing to leave out: the
	/// target is resolved live from the giver's property 77 through that object's vtable+68 and
	/// is not a field we can read, and an adult recipient — which is nearly all of them — sees
	/// exactly the same list either way.
	///
	/// `IsRing` (`sub_824303E8`) is also not applied. It drops wedding rings, but only while
	/// `sub_823588A8(dword_83496AD8)` holds — a live-session participant mask, one slot occupied
	/// and the next empty. In single player that predicate is false and the ring stays.
	/// </summary>
	private static bool CanBeGift(uint entity) =>
		TryComponent(entity, GiftComponentField, out GdbCursor gift) &&
		Globals!.TryFindInherited(gift, CanBeGiftField, (int)GdbFieldType.Bool,
			out _, out uint canBeGift, out _) &&
		canBeGift != 0;

	/// <summary>
	/// The categories `sub_8242FB28` tests for "equipped, and the last one" before it will offer
	/// an item as a gift — case 1 (Weapons) and cases 0x11..0x16 (Hats..Shoes). Note it stops at
	/// 22: masks and outfits are not in that arm of the switch.
	/// </summary>
	private static bool WearableCategory(int category) =>
		category == Category.Weapons ||
		(category >= Category.Hats && category <= Category.Shoes);

	/// <summary>
	/// ONE `None` ROW PER CLOTHING SLOT — the row that takes the garment off.
	///
	/// The guest spawns it unconditionally, right after the slot's header and before any item:
	/// `sub_829B3340` for 17..23 and `sub_829B2D58` for Suits, each a direct `sub_826CE8D0` whose
	/// String1 and String2 are both `INV_ITEM_CLOTHES_NONE_&lt;SUFFIX&gt;` and whose String4 is the
	/// category's own name, with the id and event type below.
	///
	/// What the builder does after spawning it is EMPHASIS, not existence. `sub_829B1FC0(screen,
	/// category)` asks component 15 whether anything is worn in that slot
	/// (`sub_824C4418`, which walks the worn vector at +16..+20, stride 24, matching
	/// `*(entry+16) == category`); when nothing is, the row is emphasised
	/// (`sub_823DFA98(16, name, 1)` = `g_Menu:SetItemEmphasis()`), and when something is, that
	/// emphasis is cleared. So `None` is the marker for "this slot is empty", and pressing it
	/// when the slot is NOT empty is what undresses.
	/// </summary>
	private static readonly Dictionary<int, (string Suffix, int EventType, uint Id)> NoneRows =
		new()
		{
			[Category.Suits] = ("SUITS", 90, 0x186FAu),
			[Category.Hats] = ("HATS", 91, 0x186FBu),
			[Category.Coats] = ("COATS", 92, 0x186FCu),
			[Category.Shirts] = ("SHIRTS", 93, 0x186FDu),
			[Category.Gloves] = ("GLOVES", 94, 0x186FEu),
			[Category.Pants] = ("PANTS", 95, 0x186FFu),
			[Category.Shoes] = ("SHOES", 96, 0x18700u),
			[Category.Accessories] = ("MASKS", 97, 0x18701u),
		};

	/// <summary>The dye list's operation: rows are what the held dye can go on.</summary>
	public const string DyeOperation = "dye";

	private static readonly uint AppearanceComponentField =
		Engine_GameHash.Fnv1("AppearanceModifierComponent");
	private static readonly uint DyeTexturesField = Engine_GameHash.Fnv1("DyeTextures");
	private static readonly uint[] DyeLayerFields =
	{
		Engine_GameHash.Fnv1("Layer1"), Engine_GameHash.Fnv1("Layer2"),
	};

	/// <summary>
	/// Whether the item takes a dye in this slot — the guest's own test `sub_826CB120`: its
	/// def has `DyeTextures` and that names a `Layer1` (slot 0) or `Layer2` (slot 1). The guest
	/// repeats the test before it dyes, so an unreadable database offers the row and leaves
	/// the refusal to it.
	/// </summary>
	private static bool TakesDye(uint entity, int slot)
	{
		if (Globals is not { } globals)
		{
			return true;
		}

		return TryComponent(entity, AppearanceComponentField, out GdbCursor appearance) &&
			globals.TryFindInherited(appearance, DyeTexturesField, (int)GdbFieldType.Reference,
				out _, out uint textures, out _) &&
			textures != 0u && globals.TryLookup(textures, out GdbCursor layers) &&
			globals.TryFindInherited(layers, DyeLayerFields[slot], -1, out _, out uint layer, out _) &&
			layer != 0u && layer != GdbHash.Null;
	}

	/// <summary>
	/// THE DYE LIST. `sub_829B2168` offers every owned garment in categories 17..24, worn or
	/// not, and the hair, beard and moustache only while they are on (`sub_829B1FC0`). A press
	/// dyes slot 1 and the screen's second button dyes slot 2 (`sub_826D3108` with 0 and 1); a
	/// row per slot states the same two choices in a list. The press is the guest's TOGGLE: a
	/// slot that is already dyed is scrubbed (`sub_826CAFA8` -> `sub_826CAE08`), and the row
	/// says which it will do.
	/// </summary>
	private static IReadOnlyList<Engine_UiMenuSources.Row> DyeTargets(
		string argument,
		HashSet<int>? wanted,
		List<Refii.Host.HostBridge.InventoryRecord> records)
	{
		uint dye = Engine_UiDyeFlow.Dye;
		if (dye == 0u || wanted is null)
		{
			return None;
		}

		var rows = new List<Engine_UiMenuSources.Row>();
		var seen = new HashSet<uint>();
		Dictionary<uint, Refii.Host.HostBridge.DyeState> dyed = Refii.Host.HostBridge.GetDyeStates();

		void Offer(uint recordId, string tag, string text, string icon)
		{
			if (!seen.Add(recordId))
			{
				return;
			}

			for (int slot = 0; slot < 2; slot++)
			{
				if (!TakesDye(recordId, slot))
				{
					continue;
				}

				// The footer's own choice (`sub_829B5D68`): scrub when the slot is dyed.
				bool scrubs = dyed.TryGetValue(recordId, out Refii.Host.HostBridge.DyeState state) &&
					(slot == 0 ? state.Slot1 : state.Slot2);
				string slotTag = $"GUI_SCREEN_DYE_{(scrubs ? "SCRUB" : "APPLY")}_{slot + 1}";
				string slotText = Strings is { } book && book.TryGetString(slotTag, out string said) &&
					said.Length != 0
					? said
					: slotTag;

				rows.Add(new Engine_UiMenuSources.Row(
					Name: $"{(tag.Length != 0 ? tag : $"Item:{recordId:X8}")}:dye{slot + 1}",
					Label: $"{text} — {slotText}",
					Icon: icon,
					EventType: 0,
					Id: recordId,
					Rating: Engine_UiMenuConstants.EmptyRatingSlide,
					Committable: false,
					Ghosted: false,
					Command: $"{InventoryAction}{DyeOperation}:{dye}:{recordId}:{slot}",
					Group: string.Empty,
					GroupTag: string.Empty,
					IsHeader: false,
					Emphasised: false,
					Counted: slot == 0));
			}
		}

		bool hair = wanted.Contains(Category.Hair) || wanted.Contains(Category.Beard) ||
			wanted.Contains(Category.Moustache);
		if (hair)
		{
			foreach ((uint recordId, int category) in Refii.Host.HostBridge.GetWornItems())
			{
				if (!wanted.Contains(category))
				{
					continue;
				}

				string tag = category == Category.Hair ? "INV_ITEM_HAIR"
					: category == Category.Beard ? "INV_ITEM_HAIR_BEARD"
					: "INV_ITEM_HAIR_MOUSTACHE";
				string text = Strings is { } book && book.TryGetString(tag, out string said) &&
					said.Length != 0
					? said
					: tag;
				Offer(recordId, tag, text, NoneRowIcon);
			}
		}

		foreach (Refii.Host.HostBridge.InventoryRecord record in records)
		{
			// `Category > 16 && Category <= 24` — the guest's own range test (0x829B2574).
			if (!wanted.Contains(record.Category) ||
				record.Category <= Category.Books || record.Category > Category.Dog ||
				!Listable(record, asked: true))
			{
				continue;
			}

			string tag = NameTagOf(record);
			Offer(record.RecordId, tag, LabelOf(record, tag), IconOf(record));
		}

		Say(DyeOperation + ":" + argument, $"{rows.Count} dye row(s) for dye 0x{dye:X8}");
		return rows;
	}

	/// <summary>See <see cref="NoneRows"/>. Only the clothing screen states `wear`.</summary>
	private static void AddNoneRows(
		string operation,
		HashSet<int>? wanted,
		HashSet<int> wornCategories,
		bool grouped,
		List<Engine_UiMenuSources.Row> rows)
	{
		if (operation != "wear" || wanted is null)
		{
			return;
		}

		foreach (int category in wanted)
		{
			if (!NoneRows.TryGetValue(category, out (string Suffix, int EventType, uint Id) none))
			{
				continue;
			}

			bool wearing = wornCategories.Contains(category);
			string tag = "INV_ITEM_CLOTHES_NONE_" + none.Suffix;

			// THE LABEL IS THE RESOLVED TEXT, not the tag. `SpawnSourced` puts a non-header row's
			// `Label` straight into `StatedLabel`, which the view draws verbatim — so handing it
			// the Babel key printed `INV_ITEM_CLOTHES_NONE_HATS` across the row. Every other row
			// here resolves its tag through `LabelOf` before it is spawned; this one has to too.
			string label = Strings is { } book && book.TryGetString(tag, out string resolved) &&
				resolved.Length != 0
				? resolved
				: tag;

			rows.Add(new Engine_UiMenuSources.Row(
				Name: tag,
				Label: label,
				Icon: NoneRowIcon,
				EventType: none.EventType,
				Id: none.Id,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,

				// The id IS the guest's own — a fixed constant, not a per-population ticket — but
				// the host performs the undress itself rather than pushing an identity, because
				// the guest's answer runs the 3D preview and the presentation as well.
				Committable: false,

				// Nothing on: the row is the current state, and there is nothing to take off.
				Ghosted: false,
				Command: wearing
					? $"{InventoryAction}unequip:{none.EventType}"
					: null,
				Group: grouped ? Category.NameOf(category) : string.Empty,
				GroupTag: grouped ? Category.TagOf(category) : string.Empty,
				IsHeader: false,
				Emphasised: !wearing,

				// `None` stands for no inventory record, so the header does not count it.
				Counted: false));
		}
	}

	/// <summary>`dword_8349D930` — the icon every clothing row states, `None` included.</summary>
	private const string NoneRowIcon = @"Art\GUI\WheelIcons\icon_world_map.tga";

	/// <summary>The weapons screen's two lists — see <see cref="Weapons"/>.</summary>
	public const string WeaponsSource = "weapons";

	private static readonly uint WeaponTypeField = Engine_GameHash.Fnv1("WeaponType");

	/// <summary>
	/// `WeaponType` hangs off the item's WEAPON component, not its inventory component — checked
	/// against the shipped database: no InventoryItemComponent chain carries it, every
	/// WeaponComponent chain does (Enum, e.g. entry 21200 states 7). Reading it through
	/// `InventoryItemComponent` returned nothing for every weapon, and both halves listed empty.
	/// </summary>
	private static readonly uint WeaponComponentField = Engine_GameHash.Fnv1("WeaponComponent");

	private static bool TryWeaponType(uint entity, out uint weaponType)
	{
		weaponType = 0u;
		return TryComponent(entity, WeaponComponentField, out GdbCursor weapon) &&
			(Globals!.TryFindInherited(weapon, WeaponTypeField, (int)GdbFieldType.Enum,
				out _, out weaponType, out _) ||
			Globals!.TryFindInherited(weapon, WeaponTypeField, (int)GdbFieldType.Int,
				out _, out weaponType, out _));
	}

	/// <summary>
	/// THE WEAPONS SCREEN'S MELEE OR RANGED HALF. <paramref name="argument"/> is `melee` or
	/// `ranged`.
	///
	/// The guest keeps both halves in one category — everything the row builder buckets as
	/// `Category == 1` (`sub_829B1968`) — and splits them by the item's own `WeaponType`, a GDB
	/// field read at type code 2 (`sub_82472890`) and mapped by `sub_82561420`: values 2, 3 and 4
	/// are melee, 5 through 8 are ranged, anything else is neither and is not listed.
	///
	/// A row's action is `Inventory:equip:&lt;record&gt;`, which reaches the guest's own activation
	/// switch by NAME rather than by cursor — see the pump's DispatchInventoryAction for why that
	/// distinction is the whole point on this screen.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Weapons(string argument)
	{
		int wanted = argument.Equals("ranged", System.StringComparison.OrdinalIgnoreCase) ? 2 : 1;
		List<Refii.Host.HostBridge.InventoryRecord> records =
			Refii.Host.HostBridge.GetHeroInventory();

		var rows = new List<Engine_UiMenuSources.Row>();
		int untyped = 0;
		foreach (Refii.Host.HostBridge.InventoryRecord record in records)
		{
			// A WEAPON IS A RECORD WITH A WeaponType, whatever category the guest stamped on the
			// inventory record. The stamp is what the guest's own screen keys on, but a weapon
			// given by record id (a co-op hero's) arrived with a different stamp and the fold
			// listed nothing; the GDB's own WeaponComponent is the authority here.
			bool byType = TryWeaponType(record.RecordId, out uint weaponType);
			if ((record.Category != Category.Weapons && !byType) || !Listable(record, asked: true))
			{
				continue;
			}

			if (!byType)
			{
				++untyped;
				continue;
			}

			if (WeaponClass((int)weaponType) != wanted)
			{
				continue;
			}

			string tag = NameTagOf(record);
			rows.Add(new Engine_UiMenuSources.Row(
				Name: tag.Length != 0 ? tag : $"Item:{record.RecordId:X8}",
				Label: LabelOf(record, tag),
				Icon: IconOf(record),
				EventType: 0,
				Id: record.RecordId,
				Rating: RatingOf(record),
				Committable: false,
				Ghosted: false,
				Command: $"{InventoryAction}equip:{record.RecordId}",
				Group: string.Empty,
				GroupTag: string.Empty,
				IsHeader: false,
				Emphasised: false,
				Counted: true,
				Unviewed: !record.Viewed));
		}

		// The weapon in hand. The guest's list is component 13 (`sub_829B1968`) with the equipped
		// record emphasised (`sub_826D0C08`); a weapon a def hands out at spawn is held on
		// component 28 with no inventory record, so it is listed here, emphasised.
		if (Refii.Host.HostBridge.HeroWeapons() is { } held)
		{
			uint heldId = wanted == 2 ? held.Ranged : held.Melee;
			int at = rows.FindIndex(r => r.Id == heldId);
			if (heldId != 0u && at >= 0)
			{
				rows[at] = rows[at] with { Emphasised = true, Label = EquippedLabel(rows[at].Label) };
			}
			else if (heldId != 0u && TryWeaponType(heldId, out uint heldType) &&
				WeaponClass((int)heldType) == wanted)
			{
				var heldRecord = new Refii.Host.HostBridge.InventoryRecord(
					heldId, 1, Category.Weapons, true);
				string tag = NameTagOf(heldRecord);
				rows.Insert(0, new Engine_UiMenuSources.Row(
					Name: tag.Length != 0 ? tag : $"Item:{heldId:X8}",
					Label: EquippedLabel(LabelOf(heldRecord, tag)),
					Icon: IconOf(heldRecord),
					EventType: 0,
					Id: heldId,
					Rating: RatingOf(heldRecord),
					Committable: false,
					Ghosted: false,
					Command: string.Empty,
					Group: string.Empty,
					GroupTag: string.Empty,
					IsHeader: false,
					Emphasised: true,
					Counted: false,
					Unviewed: false));
			}
		}

		// WHAT THE RECORDS SAY THEY ARE, when nothing matched: the category the guest stamped on
		// each record is what this list keys on, and a weapon given by record id can arrive
		// stamped as something else. Once per outcome.
		string why = string.Empty;
		if (rows.Count == 0 && records.Count != 0)
		{
			var byCategory = new SortedDictionary<int, int>();
			foreach (Refii.Host.HostBridge.InventoryRecord record in records)
			{
				byCategory[record.Category] = byCategory.TryGetValue(record.Category, out int n) ? n + 1 : 1;
			}

			var parts = new List<string>();
			foreach ((int category, int n) in byCategory)
			{
				parts.Add($"{category}x{n}");
			}

			var typed = new List<string>();
			foreach (Refii.Host.HostBridge.InventoryRecord record in records)
			{
				if (TryWeaponType(record.RecordId, out uint weaponType))
				{
					typed.Add($"{record.RecordId:X8}:cat{record.Category}:type{weaponType}" +
						(Listable(record, asked: true) ? string.Empty : ":unlistable"));
				}
			}

			why = $"; categories {string.Join(" ", parts)}; records with a WeaponType: " +
				(typed.Count == 0 ? "none" : string.Join(" ", typed));
		}

		Say($"weapons:{argument}",
			$"{rows.Count} {(wanted == 2 ? "ranged" : "melee")} weapon(s) of " +
			$"{records.Count} inventory record(s)" +
			(untyped == 0 ? string.Empty : $", {untyped} weapon(s) stating no WeaponType") + why);
		return rows;
	}

	/// <summary>The record's weapon class by its own WeaponType: 1 melee, 2 ranged, 0 neither or no database.</summary>
	public static int WeaponClassOf(uint record) =>
		Globals is not null && TryWeaponType(record, out uint weaponType) ? WeaponClass((int)weaponType) : 0;

	/// <summary>`sub_82561420` — 1 melee, 2 ranged, 0 neither.</summary>
	private static int WeaponClass(int weaponType) => weaponType switch
	{
		2 or 3 or 4 => 1,
		5 or 6 or 7 or 8 => 2,
		_ => 0,
	};

	/// <summary>The action prefix the guest-tick pump answers — see `DispatchInventoryAction`.</summary>
	public const string InventoryAction = "Inventory:";

	/// <summary>
	/// Takes a leading `wear:` / `gift:` off the argument and returns it, leaving the category
	/// list behind. A leading token that parses as a number is a category, not an operation.
	/// </summary>
	private static string SplitOperation(ref string argument)
	{
		int colon = argument.IndexOf(':');
		if (colon < 0)
		{
			return string.Empty;
		}

		string head = argument[..colon];
		if (head.Length == 0 || int.TryParse(head, out _))
		{
			return string.Empty;
		}

		argument = argument[(colon + 1)..];
		return head;
	}

	/// <summary>
	/// How many distinct GROUP NAMES a request covers. Null (everything) counts as many; the
	/// eight integers that all name `KeyItems` count as one.
	/// </summary>
	private static int GroupsIn(HashSet<int>? wanted)
	{
		if (wanted is null)
		{
			return int.MaxValue;
		}

		var names = new HashSet<string>(System.StringComparer.Ordinal);
		foreach (int category in wanted)
		{
			string name = Category.NameOf(category);
			if (name.Length != 0)
			{
				names.Add(name);
			}
		}

		return names.Count;
	}

	private static HashSet<int>? ParseCategories(string argument)
	{
		if (argument.Length == 0)
		{
			return null;
		}

		var wanted = new HashSet<int>();
		foreach (string part in argument.Split(','))
		{
			if (int.TryParse(part.Trim(), out int category))
			{
				wanted.Add(category);
			}
		}

		return wanted.Count == 0 ? null : wanted;
	}

	/// <summary>
	/// Whether the guest would list this record at all — `sub_827642E0`, which every populator
	/// applies before anything else.
	///
	/// `VisibleInGUI` is the item's own veto, and it is READ ONLY WHEN THE PROPERTY EXISTS: the
	/// guest tests for presence (type 8) and only then reads the bool, so an item that states
	/// nothing is visible rather than hidden. Getting that backwards empties the list.
	///
	/// THE SUITS AND EQUIPPED EXCLUSIONS ARE NOT HERE, and that is a correction.
	///
	/// They were, and they belong to the GIFT screen alone. `sub_8242FB28` switches on the item's
	/// category at `item+16`: case 0x19 (Suits) falls straight to the reject, and cases 0x11..0x16
	/// (clothing) reject only when `sub_824C4258(component15, item)` says the item is equipped AND
	/// `*(item+12) <= 1` leaves no spare. That whole function is reached only on `*a1 == 1 || 5`,
	/// the gift modes, and its default path is the `CanBeGift` test.
	///
	/// The clothing screen's own builder applies NEITHER. `sub_829B3D28` walks the inventory and
	/// spawns every record that passes this and states a category in 17..24 — no equipped test at
	/// all — which is why the worn hat is a row on the real screen and why the guest's header read
	/// `Headgear (6)` over what we were listing as five.
	/// </summary>
	private static bool Listable(Refii.Host.HostBridge.InventoryRecord record, bool asked)
	{
		if (Globals is null)
		{
			return true;
		}

		return !TryItemField(record.RecordId, VisibleField, (int)GdbFieldType.Bool,
			out uint visible) || visible != 0;
	}

	/// <summary>
	/// The item's display text: its `NameTag` through the string book, with the stack count
	/// appended the way the shipped screens do it ("Silver Key x5"). Falls back to the GDB key
	/// so an item with no tag is still identifiable rather than blank.
	/// </summary>
	/// <summary>The item's `NameTag`, off its InventoryItemComponent — see TryComponent.</summary>
	private static string NameTagOf(Refii.Host.HostBridge.InventoryRecord record) =>
		ItemString(record.RecordId, NameTagField);

	/// <summary>
	/// The row's display text: resolves via Engine_ItemNames (or Babel tag fallback), with the stack
	/// count appended the way the guest does it — `L" x"` then the number (`word_820CE5E0`, 0x826CE31C),
	/// so "Silver Key x5". An unresolved tag shows verbatim, which is what the guest does too
	/// (`sub_8230E0E8`'s third fallback copies the raw tag).
	///
	/// An OUTFIT reads its name alone: it is worn as its pieces, so how many copies of the suit
	/// item are carried says nothing the player can use — a remaster choice over the guest's
	/// suffix, which it appends to every row.
	/// </summary>
	private static string LabelOf(Refii.Host.HostBridge.InventoryRecord record, string tag)
	{
		// Try resolving via Engine_ItemNames first (unified DRY path matching editor & item pages):
		if (Globals is { } gdb)
		{
			string displayName = DisplayNameOf(gdb, record.RecordId);
			if (displayName.Length != 0)
			{
				return record.Count > 1 ? $"{displayName} x{record.Count}" : displayName;
			}
		}

		string text = tag.Length == 0
			? $"0x{record.RecordId:X8}"
			: Strings is { } book && book.TryGetString(tag, out string resolved) &&
				resolved.Length != 0
					? resolved
					: tag;

		return record.Count > 1 && record.Category != Category.Suits ? $"{text} x{record.Count}" : text;
	}

	/// <summary>
	/// The item's resolved display name, or empty when the item page's resolver yields nothing
	/// usable (the caller then falls back to the Babel tag). Cached per record: `BuildOne` is a
	/// full item read — definition, appearance, every stat component, augments — and the Items
	/// screen asked it for every carried item in every one of its nine categories on every
	/// open, and again on every refresh sample. The name depends only on the GDB and the text
	/// banks it resolves through, so an entry is good while those are the same instances.
	/// </summary>
	private static string DisplayNameOf(GdbMulti gdb, uint recordId)
	{
		Refii.Text.TextBank? bank = Engine_BnkTextBankLoader.Shared;
		if (DisplayNames.TryGet(recordId, out string? name, gdb, bank))
		{
			return name;
		}

		name = string.Empty;
		if (Engine_ItemDetails.BuildOne(gdb, recordId) is { } detail)
		{
			string resolved = Engine_ItemNames.DisplayNameOf(detail);
			if (!string.IsNullOrEmpty(resolved) && resolved != detail.Label)
			{
				name = resolved;
			}
		}

		DisplayNames.Put(recordId, name, gdb, bank);
		return name;
	}

	private static readonly Engine_UiIdentityCache<uint, string> DisplayNames = new();

	private static string IconOf(Refii.Host.HostBridge.InventoryRecord record) =>
		ItemString(record.RecordId, IconField);

	/// <summary>
	/// The star strip. Inherited like everything else — a suit takes its rating from the clothing
	/// record above it — and defaulted to the empty slide rather than to one star, so an item
	/// that states none draws no stars instead of the wrong number.
	/// </summary>
	private static int RatingOf(Refii.Host.HostBridge.InventoryRecord record)
	{
		// `sub_826CE230` computes this and passes it as SpawnButton's NINTH argument, which is
		// `Float4`. Three arms, switched on the category:
		//
		//   Augmentations, Dyes, Books  -> the constant 11 (`li r29, 0xB` at 0x826CE50C)
		//   Weapons, Accessories        -> an augment-SLOT code, not a rating: 2 per filled slot
		//                                  and 1 per empty one, slot 1 on the hundreds digit —
		//                                  see Engine_UiItemStats.AugmentSlotCode.
		//   everything else             -> GDB `Rating` DOUBLED (`slwi r29, r3, 1`, 0x826CE524)
		//
		// The doubling is the guest's, so it is kept: whatever turns `Float4` into one to three
		// stars lives in the shipped Lua and expects the doubled number.
		if (record.Category is Category.Augmentations or Category.Dyes or Category.Books)
		{
			return AugmentedRating;
		}

		// An outfit is its pieces, and its own `Rating` grades nothing the player wears — the
		// row reads its name alone (a remaster choice, like the dropped stack count).
		if (record.Category == Category.Suits)
		{
			return Engine_UiMenuConstants.EmptyRatingSlide;
		}

		if (record.Category is Category.Weapons or Category.Accessories)
		{
			return Engine_UiItemStats.AugmentSlotCode(
				record.RecordId, Refii.Host.HostBridge.GetItemAugments(record.RecordId));
		}

		return TryItemNumber(record.RecordId, RatingField, out uint rating)
			? (int)rating * 2
			: Engine_UiMenuConstants.EmptyRatingSlide;
	}

	/// <summary>`li r29, 0xB` — the constant the augment, dye and book categories carry.</summary>
	private const int AugmentedRating = 11;
}
