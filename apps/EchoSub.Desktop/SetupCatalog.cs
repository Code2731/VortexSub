using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoSub.Desktop;

internal sealed record SetupPayload(string File, long Bytes, string Sha256, string Url);
internal sealed record SetupInstalledFile(string Path, long Bytes, string Sha256);
internal sealed record SetupAsset(string Key, string Id, string Role, string License, string LicenseSource,
    IReadOnlyList<SetupPayload> Payloads, IReadOnlyList<SetupInstalledFile> Files, long? InstalledBytes)
{
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public IReadOnlyList<string> RequiredBackends { get; init; } = [];
    public IReadOnlyList<string> InputProfiles { get; init; } = [];
}
internal sealed record SetupProfile(string Id, IReadOnlyList<string> Assets, string Os, string Architecture,
    IReadOnlyList<string> Backends, IReadOnlyList<string> Languages, string? Target, string? InputProfile,
    bool Recommended = false, string? Description = null);

// Pure catalog resolution. Research paths, installed_verified and past consent are deliberately ignored.
internal sealed class SetupCatalog
{
    internal const int MaxJsonBytes = 1_048_576;
    public IReadOnlyDictionary<string, SetupAsset> Assets { get; }
    public IReadOnlyDictionary<string, SetupProfile> Profiles { get; }

    private SetupCatalog(Dictionary<string, SetupAsset> assets, Dictionary<string, SetupProfile> profiles)
    {
        Assets = new ReadOnlyDictionary<string, SetupAsset>(assets);
        Profiles = new ReadOnlyDictionary<string, SetupProfile>(profiles);
    }

    public static SetupCatalog Embedded()
    {
        string Read(string name)
        {
            using var stream = typeof(SetupCatalog).Assembly.GetManifestResourceStream(name)
                ?? throw Invalid("missing-embedded-manifest");
            if (stream.Length > MaxJsonBytes) throw Invalid("manifest-too-large");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        return Load(Read("benchmarks/first-run-policy.json"), Read);
    }

    public static SetupCatalog Load(string policy, Func<string, string> readManifest)
    {
        using var document = Parse(policy);
        var root = document.RootElement;
        Version(root);
        var assets = new Dictionary<string, SetupAsset>(StringComparer.Ordinal);
        var profiles = new Dictionary<string, SetupProfile>(StringComparer.Ordinal);
        foreach (var reference in List(root, "assets", 32))
        {
            var key = Text(reference, "key");
            if (!Regex.IsMatch(key, @"\A[a-z0-9][a-z0-9._-]{0,63}\z")) throw Invalid("invalid-asset-key");
            Relative(key);
            var manifest = Text(reference, "manifest");
            if (manifest is not ("benchmarks/model-downloads.json" or "benchmarks/vad-assets.json" or
                "benchmarks/translation-research-models.json" or "benchmarks/translation-research-runtimes.json"))
                throw Invalid("unsupported-manifest");
            using var source = Parse(readManifest(manifest));
            var data = source.RootElement;
            Version(data);
            var id = Text(reference, "id");
            var role = Text(reference, "role");
            JsonElement entry;
            if (manifest.EndsWith("translation-research-runtimes.json", StringComparison.Ordinal))
            {
                if (Text(data, "id") != id) throw Invalid("missing-asset-reference");
                if (role != "translation-runtime") throw Invalid("role-mismatch");
                entry = data;
            }
            else
            {
                var entries = List(data, manifest.EndsWith("vad-assets.json", StringComparison.Ordinal) ? "assets" : "models", 128);
                var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var item in entries)
                    if (!index.TryAdd(Text(item, "id"), item)) throw Invalid("duplicate-manifest-id");
                if (!index.TryGetValue(id, out entry)) throw Invalid("missing-asset-reference");
                var expectedRole = manifest.EndsWith("vad-assets.json", StringComparison.Ordinal)
                    ? (entry.TryGetProperty("archive", out _) ? "vad-runtime" : "vad") : Text(entry, "role");
                if (role != expectedRole) throw Invalid("role-mismatch");
            }
            var asset = Resolve(key, id, role, entry) with
            {
                Platforms = Strings(reference, "platforms"),
                RequiredBackends = reference.TryGetProperty("backends", out _) ? Strings(reference, "backends") : [],
                InputProfiles = role == "translation" ? Strings(reference, "input_profiles") : []
            };
            if (!assets.TryAdd(key, asset) || assets.Values.Count(a => a.Id == id) != 1)
                throw Invalid("duplicate-asset");
        }
        foreach (var item in List(root, "profiles", 32))
        {
            var profile = new SetupProfile(Text(item, "id"), Strings(item, "assets"), Text(item, "os"),
                Text(item, "architecture"), Strings(item, "backends"), Strings(item, "languages"),
                Optional(item, "target"), Optional(item, "input_profile"),
                OptionalBool(item, "recommended"), Optional(item, "description"));
            if (profile.Assets.Any(key => !assets.ContainsKey(key))) throw Invalid("missing-profile-asset");
            var selected = profile.Assets.Select(key => assets[key]).ToArray();
            if (selected.Any(asset => (!asset.Platforms.Contains("any") && !asset.Platforms.Contains($"{profile.Os}/{profile.Architecture}")) ||
                asset.RequiredBackends.Any(backend => !profile.Backends.Contains(backend)))) throw Invalid("unsupported-profile-platform");
            foreach (var required in new[] { "asr", "vad", "vad-runtime" })
                if (selected.Count(asset => asset.Role == required) != 1) throw Invalid("incomplete-profile");
            bool translation = profile.Target is not null;
            if (selected.Count(a => a.Role == "translation") != (translation ? 1 : 0) ||
                selected.Count(a => a.Role == "translation-runtime") != (translation ? 1 : 0) ||
                translation != (profile.InputProfile is not null)) throw Invalid("incomplete-translation-profile");
            var model = selected.SingleOrDefault(a => a.Role == "translation");
            if (model is not null && !model.InputProfiles.Contains(profile.InputProfile!))
                throw Invalid("model-profile-mismatch");
            if (profile.Backends.Count(backend => backend is "asr:cpu" or "asr:cuda") != 1 ||
                profile.Backends.Count(backend => backend is "translation:cpu" or "translation:cuda") != (translation ? 1 : 0) ||
                profile.Backends.Any(backend => backend is not ("asr:cpu" or "asr:cuda" or "translation:cpu" or "translation:cuda")))
                throw Invalid("invalid-profile-backend");
            if (!profiles.TryAdd(profile.Id, profile)) throw Invalid("duplicate-profile");
        }
        return new(assets, profiles);
    }

    private static SetupAsset Resolve(string key, string id, string role, JsonElement entry)
    {
        if (role is not ("asr" or "vad" or "vad-runtime" or "translation" or "translation-runtime"))
            throw Invalid("invalid-role");
        var license = Text(entry, "license");
        var licenseSource = Https(Optional(entry, "license_source") ?? Optional(entry, "license_url") ?? Text(entry, "source"));
        var payloads = new List<SetupPayload>();
        var files = new List<SetupInstalledFile>();
        long? installedBytes = null;
        bool archive = role.EndsWith("runtime", StringComparison.Ordinal);
        if (role == "translation-runtime")
        {
            foreach (var asset in List(entry, "assets", 32))
                payloads.Add(new(Relative(Text(asset, "file")), Positive(asset, "bytes"), Hash(Text(asset, "sha256")), Https(Text(asset, "url"))));
        }
        else
        {
            string file = Relative(Text(entry, archive ? "archive" : "file"));
            string url;
            if (Optional(entry, "url") is { } direct) url = Https(direct);
            else
            {
                var repository = Text(entry, "repository");
                var revision = Text(entry, "revision");
                if (!Regex.IsMatch(repository, @"\A[\w.-]+/[\w.-]+\z") ||
                    !Regex.IsMatch(revision, @"\A[0-9a-fA-F]{40}\z")) throw Invalid("unpinned-model-source");
                url = Https($"https://huggingface.co/{repository}/resolve/{revision}/{Uri.EscapeDataString(file)}?download=true");
            }
            var bytes = Positive(entry, archive ? "archive_bytes" : "bytes");
            var hash = Hash(Text(entry, archive ? "archive_sha256" : "sha256"));
            payloads.Add(new(file, bytes, hash, url));
            if (!archive) { files.Add(new(file, bytes, hash)); installedBytes = bytes; }
        }
        // Archives remain incomplete until the source manifest describes every installed file.
        if (archive && entry.TryGetProperty("installed_files", out _))
        {
            foreach (var item in List(entry, "installed_files", 512))
                files.Add(new(Relative(Text(item, "path")), Positive(item, "bytes"), Hash(Text(item, "sha256"))));
            installedBytes = Positive(entry, "installed_bytes");
            try { if (files.Sum(f => f.Bytes) != installedBytes) throw Invalid("installed-size-mismatch"); }
            catch (OverflowException) { throw Invalid("installed-size-overflow"); }
        }
        if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count ||
            payloads.Select(p => p.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payloads.Count)
            throw Invalid("duplicate-file");
        return new(key, id, role, license, licenseSource, payloads.AsReadOnly(), files.AsReadOnly(), installedBytes);
    }

    // No filesystem or network access. Local research status cannot change consent identity.
    public string Fingerprint(SetupProfile profile)
    {
        var data = new { Profile = profile, Assets = profile.Assets.Order(StringComparer.Ordinal).Select(key => Assets[key]).ToArray() };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
    }

    private static JsonDocument Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw Invalid("manifest-too-large");
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            try { UniqueProperties(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw Invalid("invalid-json"); }
    }
    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("duplicate-json-property");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) UniqueProperties(child);
    }
    private static void Version(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("schema_version", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version) || version != 1)
            throw Invalid("unsupported-schema");
    }
    private static JsonElement.ArrayEnumerator List(JsonElement element, string name, int limit)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() is 0 || value.GetArrayLength() > limit) throw Invalid("invalid-list");
        return value.EnumerateArray();
    }
    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        var values = List(element, name, 32).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : "").ToArray();
        if (values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Invalid("invalid-string-list");
        return Array.AsReadOnly(values);
    }
    private static string? Optional(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : Text(element, name);
    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 2048) throw Invalid("invalid-text");
        return value.GetString()!;
    }
    private static long Positive(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long bytes) || bytes <= 0)
            throw Invalid("invalid-size");
        return bytes;
    }
    private static string Hash(string value) => Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z") ? value.ToLowerInvariant() : throw Invalid("invalid-hash");
    private static string Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? value : throw Invalid("invalid-url");
    private static string Relative(string value)
    {
        if (value.Length > 240 || value.Contains('\\') || value.Contains(':') || value.StartsWith('/') ||
            value.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                part.Any(c => c < 32 || "<>\"|?*".Contains(c)))) throw Invalid("invalid-relative-path");
        return value;
    }
    private static InvalidDataException Invalid(string reason) => new($"Setup catalog: {reason}");
}
