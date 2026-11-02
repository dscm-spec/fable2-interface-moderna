using System;
using System.Collections.Generic;

using Godot;

namespace Refii.Gdb;

/// <summary>
/// The merged supplemental GDB every runtime reader shares, built once. Whoever mounts binds the
/// resolver (`Engine_GameAssets` in the game, `addons/bnk_mount` in the editor); unbound, every
/// lookup silently answers "no such record".
/// </summary>
public static class Engine_GameGdb
{
	private static readonly object BuildLock = new();

	private static Func<string, byte[]?>? _resolver;

	private static GdbMulti? _multi;

	private static bool _built;

	private static Func<string, byte[]?>? _baseResolver;

	private static GdbMulti? _baseMulti;

	private static bool _baseBuilt;

	/// <summary>Where to read the GDB blobs from. Clears anything already built.</summary>
	public static void SetResolver(Func<string, byte[]?>? resolver)
	{
		lock (BuildLock)
		{
			_resolver = resolver;
			_multi = null;
			_built = false;
		}
	}

	/// <summary>Binds a resolver for unpatched base game files.</summary>
	public static void SetBaseResolver(Func<string, byte[]?>? baseResolver)
	{
		lock (BuildLock)
		{
			_baseResolver = baseResolver;
			_baseMulti = null;
			_baseBuilt = false;
		}
	}

	/// <summary>Forces re-reading the active GDB blobs on next access.</summary>
	public static void Invalidate()
	{
		lock (BuildLock)
		{
			_multi = null;
			_built = false;
		}
	}

	/// <summary>
	/// Loads raw GDB binary blobs for all supplemental databases, optionally including companion Title Update (.gdb.tu) patches.
	/// </summary>
	public static List<byte[]?> LoadSupplementalBlobs(Func<string, byte[]?> resolver, bool includeTu = true)
	{
		var blobs = new List<byte[]?>();
		foreach (string path in Refii.Level.Engine_LevelProps.SupplementalGdbPaths)
		{
			byte[]? blob = resolver(path) ?? resolver("data/" + path);
			blobs.Add(blob);

			if (includeTu)
			{
				byte[]? tuBlob = resolver(path + ".tu") ?? resolver("data/" + path + ".tu");
				if (tuBlob is not null)
				{
					blobs.Add(tuBlob);
				}
			}
		}
		return blobs;
	}

	private static byte[]? DefaultBaseFileResolver(string virtualPath)
	{
		string gameDir = Refii.Core.Engine_GameData.Resolve();
		if (string.IsNullOrEmpty(gameDir))
		{
			return null;
		}

		string norm = virtualPath.Replace('\\', '/').TrimStart('/');
		string full = System.IO.Path.Combine(gameDir, norm);
		if (System.IO.File.Exists(full))
		{
			return System.IO.File.ReadAllBytes(full);
		}

		string underData = System.IO.Path.Combine(gameDir, "data", norm);
		if (System.IO.File.Exists(underData))
		{
			return System.IO.File.ReadAllBytes(underData);
		}

		return null;
	}

	/// <summary>
	/// One unpatched database by path — for a database outside <see cref="BaseDatabase"/>'s
	/// supplemental set (a level's), so a row overlay on it resolves names against its own records.
	/// </summary>
	public static byte[]? ReadBaseBytes(string virtualPath)
	{
		Func<string, byte[]?> resolver;
		lock (BuildLock)
		{
			resolver = _baseResolver ?? DefaultBaseFileResolver;
		}

		return resolver(virtualPath) ?? resolver("data/" + virtualPath);
	}

	/// <summary>The unpatched base database before any mod overlays are applied.</summary>
	public static GdbMulti? BaseDatabase
	{
		get
		{
			lock (BuildLock)
			{
				if (_baseBuilt)
				{
					return _baseMulti;
				}

				Func<string, byte[]?> resolver = _baseResolver ?? DefaultBaseFileResolver;
				var blobs = LoadSupplementalBlobs(resolver, includeTu: false);
				_baseMulti = GdbMulti.Build(blobs);
				if (_baseMulti is not null || _baseResolver is not null)
				{
					_baseBuilt = true;
				}
				return _baseMulti;
			}
		}
	}

	/// <summary>The merged database, built on first use; null before a resolver is bound.</summary>
	public static GdbMulti? Database
	{
		get
		{
			lock (BuildLock)
			{
				if (_built || _resolver is null)
				{
					return _multi;
				}

				var blobs = LoadSupplementalBlobs(_resolver, includeTu: true);
				var absent = new List<string>();
				foreach (string path in Refii.Level.Engine_LevelProps.SupplementalGdbPaths)
				{
					if ((_resolver(path) ?? _resolver("data/" + path)) is null)
					{
						absent.Add(path);
					}
				}

				_multi = GdbMulti.Build(blobs);
				_built = true;
				LogPrint($"[game-gdb] {blobs.Count - absent.Count}/{blobs.Count} database(s) read, " +
					$"{_multi?.RecordCount ?? 0} record(s)" +
					(absent.Count == 0 ? string.Empty : $" — NOT FOUND: {string.Join(", ", absent)}") +
					(_multi is null || _multi.RecordCount == 0
						? "  *** every GDB-backed field will be empty ***"
						: string.Empty));

				return _multi;
			}
		}
	}

	/// <summary>
	/// D3c: the merged database only if it has ALREADY been built — this never builds it, so a
	/// warm-up polling it cannot move the build itself onto its own frame.
	/// </summary>
	public static GdbMulti? DatabaseIfBuilt
	{
		get
		{
			lock (BuildLock)
			{
				return _built ? _multi : null;
			}
		}
	}

	private static void LogPrint(string message)
	{
		if (AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
			System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		try { GD.Print(message); } catch { }
	}

	/// <summary>A record by its hash, for ids the guest already resolved.</summary>
	public static bool TryLookupHash(uint hash, out GdbCursor cursor)
	{
		cursor = default;
		GdbMulti? multi = Database;
		return multi is not null && hash != 0u && multi.TryLookup(hash, out cursor);
	}

	/// <summary>A record by its authored name, via FNV-1.</summary>
	public static bool TryLookup(string recordName, out GdbCursor cursor)
	{
		cursor = default;
		GdbMulti? multi = Database;
		return multi is not null && !string.IsNullOrEmpty(recordName) &&
			multi.TryLookup(Refii.Core.Engine_GameHash.Fnv1(recordName), out cursor);
	}
}
