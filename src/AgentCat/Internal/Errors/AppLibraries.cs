using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AgentCat.Internal.Errors;

internal enum LibraryKind
{
    Unlisted,
    Project,
    Package,
}

// How the app's deps.json lists each assembly: built from one of the app's own projects, or shipped in a NuGet
// package. Package PDBs carry their authors' build paths, so file paths cannot tell the two apart. Apps without
// a deps.json (single-file, NativeAOT) list nothing.
internal sealed class AppLibraries
{
    private const string DepsFilesKey = "APP_CONTEXT_DEPS_FILES";

    private static readonly char[] DepsFileSeparators = { ';' };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<AppLibraries> LazyCurrent = new(() => FromDepsFiles(AppContext.GetData(DepsFilesKey) as string));

    private readonly Dictionary<string, LibraryKind> _kinds;

    private AppLibraries(Dictionary<string, LibraryKind> kinds) => _kinds = kinds;

    internal static AppLibraries Empty { get; } = new(new Dictionary<string, LibraryKind>());

    internal static AppLibraries Current => LazyCurrent.Value;

    internal LibraryKind KindOf(string? assemblyName)
        => assemblyName is not null && _kinds.TryGetValue(assemblyName, out var kind) ? kind : LibraryKind.Unlisted;

    // The host lists the app's deps.json first, then the shared frameworks' ones.
    internal static AppLibraries FromDepsFiles(string? depsFiles)
    {
        try
        {
            var files = depsFiles?.Split(DepsFileSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (files is null || files.Length == 0 || !File.Exists(files[0]))
            {
                return Empty;
            }

            using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, ReadOptions);
            return From(document.RootElement);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    internal static AppLibraries Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, ReadOptions);
            return From(document.RootElement);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    private static AppLibraries From(JsonElement root)
    {
        var libraries = new Dictionary<string, LibraryKind>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("libraries", out var listed)
            && listed.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in listed.EnumerateObject())
            {
                var kind = KindOfType(library.Value);
                if (kind != LibraryKind.Unlisted)
                {
                    libraries[library.Name] = kind;
                }
            }
        }

        if (libraries.Count == 0)
        {
            return Empty;
        }

        var kinds = new Dictionary<string, LibraryKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var slash = library.Key.IndexOf('/');
            Add(kinds, slash < 0 ? library.Key : library.Key.Substring(0, slash), library.Value);
        }

        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Object)
        {
            foreach (var target in targets.EnumerateObject())
            {
                if (target.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var library in target.Value.EnumerateObject())
                {
                    if (libraries.TryGetValue(library.Name, out var kind) && library.Value.ValueKind == JsonValueKind.Object)
                    {
                        AddAssemblies(kinds, library.Value, "runtime", kind);
                        AddAssemblies(kinds, library.Value, "runtimeTargets", kind);
                    }
                }
            }
        }

        return new AppLibraries(kinds);
    }

    private static LibraryKind KindOfType(JsonElement library)
    {
        if (library.ValueKind != JsonValueKind.Object
            || !library.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            return LibraryKind.Unlisted;
        }

        return type.GetString() switch
        {
            "project" => LibraryKind.Project,
            "package" => LibraryKind.Package,
            _ => LibraryKind.Unlisted,
        };
    }

    private static void AddAssemblies(Dictionary<string, LibraryKind> kinds, JsonElement library, string assetGroup, LibraryKind kind)
    {
        if (!library.TryGetProperty(assetGroup, out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var asset in assets.EnumerateObject())
        {
            var path = asset.Name;
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var start = path.LastIndexOf('/') + 1;
                Add(kinds, path.Substring(start, path.Length - start - 4), kind);
            }
        }
    }

    // An assembly that one of the app's own projects builds stays the app's, whatever else lists the name.
    private static void Add(Dictionary<string, LibraryKind> kinds, string assemblyName, LibraryKind kind)
    {
        if (assemblyName.Length > 0 && !(kinds.TryGetValue(assemblyName, out var existing) && existing == LibraryKind.Project))
        {
            kinds[assemblyName] = kind;
        }
    }
}
