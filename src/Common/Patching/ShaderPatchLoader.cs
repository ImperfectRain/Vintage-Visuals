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

            // Client-only, does not affect gameplay: these assets never reach
            // the server and must not participate in server asset syncing.
            var unused = new AssetCategory(code, false, EnumAppSide.Client);
        }

        /// <summary>
        /// (Re)reads every patch asset and installs the result into
        /// <paramref name="patcher"/>. Safe to call repeatedly — the game
        /// reloads shaders on graphics setting changes and we must re-derive
        /// patches from disk each time so live-edited YAML takes effect.
        /// </summary>
        public void LoadInto(ShaderPatcher patcher)
        {
            var patches = new List<ShaderPatch>();

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

                try
                {
                    patches.AddRange(ParseFile(asset.ToText(), defaultGroup, origin));
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
                                 assets.Count + " file(s).");
        }

        private IEnumerable<ShaderPatch> ParseFile(string yaml, string defaultGroup, string origin)
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
                result.Add(BuildPatch(entries[i], defaultGroup, origin + " entry " + (i + 1)));
            }

            return result;
        }

        private ShaderPatch BuildPatch(PatchEntry entry, string defaultGroup, string origin)
        {
            if (string.IsNullOrWhiteSpace(entry.Filename))
            {
                throw new ArgumentException(origin + ": 'filename' is required. " +
                    "Unlike some other shader mods this loader has no 'apply to every shader' mode — " +
                    "an untargeted patch is almost always a mistake and is very hard to debug.");
            }

            string group = string.IsNullOrWhiteSpace(entry.Group) ? defaultGroup : entry.Group.Trim();
            string content = ResolveContent(entry, origin);
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

        private string ResolveContent(PatchEntry entry, string origin)
        {
            if (string.IsNullOrEmpty(entry.Snippet)) return entry.Content ?? "";

            if (entry.Content != null)
            {
                throw new ArgumentException(origin + ": 'content' and 'snippet' are mutually exclusive — " +
                    "having two sources for the injected GLSL makes the applied result ambiguous.");
            }

            var location = new AssetLocation(_domain, SnippetCategory + "/" + entry.Snippet);
            IAsset asset = _capi.Assets.TryGet(location);

            if (asset == null)
            {
                throw new ArgumentException(origin + ": snippet '" + entry.Snippet + "' not found at " + location +
                    ". Snippet filenames include their extension, e.g. 'colorgrade.glsl'.");
            }

            return asset.ToText();
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
