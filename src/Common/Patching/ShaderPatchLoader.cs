using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// Reads <c>assets/vintagevisuals/shaderpatches/*.yaml</c> into
    /// <see cref="ShaderPatch"/> objects and hands them to a
    /// <see cref="ShaderPatcher"/>.
    ///
    /// Patch bodies live in <c>shadersnippets/</c> rather than inline in the
    /// YAML whenever they are more than a line or two: GLSL embedded in YAML
    /// block scalars is unreadable, un-diffable and impossible to syntax
    /// highlight. The <c>snippet:</c> key exists for exactly that.
    /// </summary>
    public sealed class ShaderPatchLoader
    {
        public const string PatchCategory = "shaderpatches";
        public const string SnippetCategory = "shadersnippets";

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;
        private readonly string _domain;

        public ShaderPatchLoader(ICoreClientAPI capi, ILogger logger, string domain)
        {
            _capi = capi;
            _logger = logger;
            _domain = domain;

            EnsureCategory(PatchCategory);
            EnsureCategory(SnippetCategory);
        }

        /// <summary>
        /// Registers a custom asset category if the game (or another mod) has
        /// not already. The AssetCategory constructor registers itself into a
        /// static dictionary as a side effect, so constructing a duplicate
        /// would quietly replace a shared instance — check first.
        /// </summary>
        private static void EnsureCategory(string code)
        {
            if (AssetCategory.categories.ContainsKey(code)) return;

            // The constructor registers itself into AssetCategory.categories,
            // which is the actual point of this call - the instance is not
            // needed here. Client-only and not gameplay-affecting: these assets
            // never reach the server and must not join server asset syncing.
            new AssetCategory(code, false, EnumAppSide.Client);
        }

        /// <summary>
        /// (Re)reads every patch asset and installs the result into
        /// <paramref name="patcher"/>. Safe to call repeatedly — the game
        /// reloads shaders on graphics setting changes and we must re-derive
        /// patches from disk each time so live-edited YAML takes effect.
        /// </summary>
        public void LoadInto(ShaderPatcher patcher)
        {
            LoadInto(patcher, null);
        }

        /// <summary>
        /// As above, but skips any group <paramref name="groupEnabled"/> rejects.
        ///
        /// A disabled subsystem must not patch shaders at all — not patch them
        /// and then decline to use them. A uniform this mod never uploads is a
        /// safe no-op, but the GLSL around it is not: it still has to compile,
        /// it still occupies a sampler, and if any of that goes wrong the
        /// player loses whatever that shader draws. For chunkopaque.fsh that is
        /// the entire world, and "turn the feature off" then has to actually
        /// restore vanilla source rather than merely quieten the feature.
        ///
        /// This is the escape hatch a config flag has to be. It cost a session
        /// of broken rendering to learn that a flag which only silences the
        /// effect is not one.
        /// </summary>
        public void LoadInto(ShaderPatcher patcher, System.Func<string, bool> groupEnabled)
        {
            var patches = new List<ShaderPatch>();
            var skipped = new List<string>();

            _capi.Assets.Reload(AssetCategory.categories[PatchCategory]);
            _capi.Assets.Reload(AssetCategory.categories[SnippetCategory]);

            List<IAsset> assets = _capi.Assets.GetMany(PatchCategory, _domain);

            if (assets == null || assets.Count == 0)
            {
                _logger.Warning("[VintageVisuals] no shader patch files found under " +
                                _domain + ":" + PatchCategory + "/ — no visual subsystem will do anything.");
                patcher.SetPatches(patches);
                return;
            }

            foreach (IAsset asset in assets)
            {
                string origin = asset.Location.ToString();

                // Group defaults to the patch file's own name, so
                // shaderpatches/colorgrade.yaml groups as "colorgrade" without
                // repeating it on every entry.
                string defaultGroup = Path.GetFileNameWithoutExtension(asset.Name);

                if (groupEnabled != null && !groupEnabled(defaultGroup))
                {
                    skipped.Add(defaultGroup);
                    continue;
                }

                try
                {
                    patches.AddRange(ParsePatchFile(asset.ToText(), defaultGroup, origin, ResolveSnippet));
                }
                catch (Exception ex)
                {
                    // A malformed patch file disables its own subsystem only.
                    _logger.Error("[VintageVisuals] CRITICAL failed to parse shader patch file " + origin +
                                  " — subsystem '" + defaultGroup + "' will be inert. " + ex.Message);
                }
            }

            patcher.SetPatches(patches);
            _logger.Notification("[VintageVisuals] loaded " + patches.Count + " shader patch(es) from " +
                                 assets.Count + " file(s)." +
                                 (skipped.Count > 0
                                     ? " Skipped " + string.Join(", ", skipped) +
                                       " — disabled in config, so " +
                                       (skipped.Count == 1 ? "its" : "their") +
                                       " target shaders stay vanilla."
                                     : ""));
        }

        /// <summary>
        /// Loads a snippet's GLSL out of the asset system. Passed to
        /// <see cref="ParsePatchFile"/> as a delegate so the parser itself has
        /// no dependency on the game API and can be exercised directly.
        /// </summary>
        private string ResolveSnippet(string snippetName)
        {
            var location = new AssetLocation(_domain, SnippetCategory + "/" + snippetName);
            IAsset asset = _capi.Assets.TryGet(location);

            if (asset == null)
            {
                throw new ArgumentException("snippet '" + snippetName + "' not found at " + location +
                    ". Snippet filenames include their extension, e.g. 'colorgrade.glsl'.");
            }

            return asset.ToText();
        }

        /// <summary>
        /// Parses one patch YAML document into patches.
        ///
        /// Static and API-free on purpose: this is where a game update actually
        /// breaks things, and a seam that can be called with a string and a
        /// stub resolver is one that can be tested without a running client.
        /// </summary>
        /// <param name="resolveSnippet">Maps a snippet filename to its GLSL.</param>
        public static IEnumerable<ShaderPatch> ParsePatchFile(string yaml, string defaultGroup, string origin,
                                                              System.Func<string, string> resolveSnippet)
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            List<PatchEntry> entries = deserializer.Deserialize<List<PatchEntry>>(yaml);
            var result = new List<ShaderPatch>();

            // An empty or comment-only file deserializes to null, not an empty list.
            if (entries == null) return result;

            for (int i = 0; i < entries.Count; i++)
            {
                result.Add(BuildPatch(entries[i], defaultGroup, origin + " entry " + (i + 1), resolveSnippet));
            }

            return result;
        }

        private static ShaderPatch BuildPatch(PatchEntry entry, string defaultGroup, string origin,
                                              System.Func<string, string> resolveSnippet)
        {
            if (string.IsNullOrWhiteSpace(entry.Filename))
            {
                throw new ArgumentException(origin + ": 'filename' is required. " +
                    "Unlike some other shader mods this loader has no 'apply to every shader' mode — " +
                    "an untargeted patch is almost always a mistake and is very hard to debug.");
            }

            string group = string.IsNullOrWhiteSpace(entry.Group) ? defaultGroup : entry.Group.Trim();
            string content = ResolveContent(entry, origin, resolveSnippet);
            ShaderPatchKind kind = ParseKind(entry.Type, origin);

            Regex anchor = null;
            string anchorDescription = null;

            if (kind == ShaderPatchKind.Token)
            {
                if (string.IsNullOrEmpty(entry.Tokens))
                    throw new ArgumentException(origin + ": type 'token' requires a 'tokens' key.");

                anchor = GlslTokenPattern.Build(entry.Tokens);
                anchorDescription = "tokens `" + Collapse(entry.Tokens) + "`";
            }
            else if (kind == ShaderPatchKind.Regex)
            {
                if (string.IsNullOrEmpty(entry.Regex))
                    throw new ArgumentException(origin + ": type 'regex' requires a 'regex' key.");

                anchor = new Regex(entry.Regex, RegexOptions.Multiline | RegexOptions.CultureInvariant);
                anchorDescription = "regex `" + Collapse(entry.Regex) + "`";
            }

            return new ShaderPatch(group, entry.Filename.Trim(), kind, content,
                                   anchor, anchorDescription, entry.Optional, entry.Multiple, origin);
        }

        private static string ResolveContent(PatchEntry entry, string origin, System.Func<string, string> resolveSnippet)
        {
            if (string.IsNullOrEmpty(entry.Snippet)) return entry.Content ?? "";

            if (entry.Content != null)
            {
                throw new ArgumentException(origin + ": 'content' and 'snippet' are mutually exclusive — " +
                    "having two sources for the injected GLSL makes the applied result ambiguous.");
            }

            try
            {
                return resolveSnippet(entry.Snippet);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(origin + ": " + ex.Message);
            }
        }

        private static ShaderPatchKind ParseKind(string type, string origin)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "token": return ShaderPatchKind.Token;
                case "regex": return ShaderPatchKind.Regex;
                case "start": return ShaderPatchKind.Start;
                case "end": return ShaderPatchKind.End;
                default:
                    throw new ArgumentException(origin + ": unknown patch type '" + type +
                        "'. Valid types are token, regex, start, end.");
            }
        }

        /// <summary>Squashes newlines so a multi-line anchor stays on one log line.</summary>
        private static string Collapse(string text)
        {
            string collapsed = Regex.Replace(text, @"\s+", " ").Trim();
            return collapsed.Length <= 120 ? collapsed : collapsed.Substring(0, 117) + "...";
        }

        /// <summary>
        /// YAML shape of a single patch entry. Public properties, not fields:
        /// YamlDotNet's default type inspector only binds properties.
        /// </summary>
        private sealed class PatchEntry
        {
            public string Type { get; set; }
            public string Group { get; set; }
            public string Filename { get; set; }
            public string Content { get; set; }
            public string Snippet { get; set; }
            public string Tokens { get; set; }
            public string Regex { get; set; }
            public bool Optional { get; set; }
            public bool Multiple { get; set; }
        }
    }
}
