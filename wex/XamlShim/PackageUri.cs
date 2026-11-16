// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// ms-appx:/// names a file of the application's package. Uno resolves it in its own folder (where the shim's assets
// and Uno's fonts are), so the application's package files are given to Uno as file: URIs. The package is the
// folder of the process's executable. A resource with qualifiers (Logo.png stored as Logo.scale-200.png) is found by
// its qualified names, scale 100 first.

namespace XamlShim.Abi;

public static class PackageUri
{
    const string Scheme = "ms-appx:///";
    static readonly string root = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> mapped = new(StringComparer.OrdinalIgnoreCase);

    /* the file: URI of an ms-appx:/// value's package file (a fragment, #Family of a font, is kept), or null when the
     * value is not ms-appx:/// or the package has no such file */
    public static string? Map(string? value)
    {
        if (value == null || !value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        return mapped.GetOrAdd(value, v =>
        {
            var hash = v.IndexOf('#');
            var fragment = hash >= 0 ? v[hash..] : "";
            var relative = Uri.UnescapeDataString(hash >= 0 ? v[Scheme.Length..hash] : v[Scheme.Length..]);
            if (Find(relative) is not { } file) return null;
            var uri = new Uri(file).AbsoluteUri + fragment;
            Ccw.Log($"package file {v} is {uri}");
            return uri;
        });
    }

    static string? Find(string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir)) return null;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        foreach (var scale in new[] { "100", "125", "150", "200", "400" })
        {
            var qualified = Path.Combine(dir, $"{name}.scale-{scale}{ext}");
            if (File.Exists(qualified)) return qualified;
        }
        return Directory.EnumerateFiles(dir, $"{name}.*{ext}").FirstOrDefault();
    }
}
