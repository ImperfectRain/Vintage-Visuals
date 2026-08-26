using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VintageVisuals.Common.Patching
{
    public enum ShaderPatchKind
    {
        /// <summary>Replace a literal run of GLSL, ignoring whitespace differences.</summary>
        Token,

        /// <summary>Replace a raw regex match. Escape hatch for what Token cannot express.</summary>
        Regex,

        /// <summary>Prove an anchor exists without modifying the source.</summary>
        Assert,

        /// <summary>Insert content before an anchor, preserving the matched source.</summary>
        InsertBefore,

        /// <summary>Insert content after an anchor, preserving the matched source.</summary>
        InsertAfter,

        /// <summary>Explicitly replace an anchor with content.</summary>
        Replace,

        /// <summary>Replace an anchor with content that contains a ${MATCH} placeholder.</summary>
        Wrap,

        /// <summary>Insert immediately after the preprocessor preamble (#version / #extension).</summary>
        Start,

        /// <summary>Append to the end of the file.</summary>
        End
    }

    /// <summary>
    /// One edit against one vanilla shader file.
    ///
    /// Replacement content is treated as a **literal** string, never as a regex
    /// substitution template: GLSL is full of <c>$</c>-free but brace-heavy code
    /// and silently interpreting <c>$1</c> would be a nasty trap. New patch
    /// operations preserve matched source explicitly; only <see cref="ShaderPatchKind.Replace"/>
    /// and the legacy Token/Regex forms remove their anchors.
    /// </summary>
    public sealed class ShaderPatch
    {
        /// <summary>Subsystem this patch belongs to. All patches in a group succeed or fail together.</summary>
        public string Group { get; }

        /// <summary>Shader file to patch, e.g. "final.fsh". Compared case-insensitively.</summary>
        public string Filename { get; }

        public ShaderPatchKind Kind { get; }

        /// <summary>GLSL to insert, or to replace the match with.</summary>
        public string Content { get; }

        /// <summary>Compiled anchor. Null for <see cref="ShaderPatchKind.Start"/> and <see cref="ShaderPatchKind.End"/>.</summary>
        public Regex Anchor { get; }

        /// <summary>Human-readable anchor text, for log messages.</summary>
        public string AnchorDescription { get; }

        /// <summary>When true, a missing anchor is tolerated instead of failing the group.</summary>
        public bool Optional { get; }

        /// <summary>When true, every occurrence is replaced. Otherwise more than one match is an error.</summary>
        public bool Multiple { get; }

        /// <summary>Source asset this patch was read from, for log messages.</summary>
        public string Origin { get; }

        public ShaderPatch(string group, string filename, ShaderPatchKind kind, string content,
                           Regex anchor, string anchorDescription, bool optional, bool multiple, string origin)
        {
            Group = group;
            Filename = filename;
            Kind = kind;
            Content = content ?? "";
            Anchor = anchor;
            AnchorDescription = anchorDescription;
            Optional = optional;
            Multiple = multiple;
            Origin = origin;
        }

        public bool AppliesTo(string filename)
        {
            return string.Equals(Filename, filename, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the patched source. Throws <see cref="ShaderPatchException"/>
        /// if the anchor did not match as declared.
        /// </summary>
        public string Apply(string filename, string code)
        {
            switch (Kind)
            {
                case ShaderPatchKind.Start:
                    return ApplyStart(code);

                case ShaderPatchKind.End:
                    return code + Environment.NewLine + Content + Environment.NewLine;

                default:
                    return ApplyAnchored(filename, code);
            }
        }

        private string ApplyAnchored(string filename, string code)
        {
            MatchCollection matches = Anchor.Matches(code);

            if (matches.Count == 0)
            {
                if (Optional) return code;

                throw new ShaderPatchException(Group, filename,
                    "anchor not found: " + AnchorDescription +
                    " (from " + Origin + "). The vanilla shader most likely changed — " +
                    "re-read the installed game's assets/game/shaders/" + filename + " and update the patch.");
            }

            if (matches.Count > 1 && !Multiple)
            {
                throw new ShaderPatchException(Group, filename,
                    "anchor matched " + matches.Count + " times but the patch expects exactly one: " +
                    AnchorDescription + " (from " + Origin + "). Narrow the anchor, or set 'multiple: true' " +
                    "if replacing every occurrence is actually intended.");
            }

            return Anchor.Replace(code, m => ReplacementFor(m));
        }

        private string ReplacementFor(Match match)
        {
            switch (Kind)
            {
                case ShaderPatchKind.Assert:
                    return match.Value;

                case ShaderPatchKind.InsertBefore:
                    return Content + Environment.NewLine + match.Value;

                case ShaderPatchKind.InsertAfter:
                    return match.Value + Environment.NewLine + Content;

                case ShaderPatchKind.Wrap:
                    if (!Content.Contains("${MATCH}"))
                    {
                        throw new ShaderPatchException(Group, Filename,
                            "wrap patch from " + Origin + " does not contain ${MATCH}; use replace for deliberate deletion.");
                    }

                    return Content.Replace("${MATCH}", match.Value);

                case ShaderPatchKind.Replace:
                case ShaderPatchKind.Token:
                case ShaderPatchKind.Regex:
                    return Content;

                default:
                    throw new ShaderPatchException(Group, Filename,
                        "unsupported patch operation " + Kind + " from " + Origin);
            }
        }

        /// <summary>
        /// Inserts <see cref="Content"/> after the preprocessor preamble.
        ///
        /// <c>#version</c> must be the first non-comment directive in a GLSL
        /// unit and <c>#extension</c> must precede any non-preprocessor code,
        /// so injected uniforms and defines have to land after both — but
        /// before everything else, so later code can use them.
        /// </summary>
        private string ApplyStart(string code)
        {
            var sb = new StringBuilder(code.Length + Content.Length + 2);

            using (var reader = new StringReader(code))
            {
                string line;
                bool inserted = false;

                while ((line = reader.ReadLine()) != null)
                {
                    if (!inserted && !IsPreambleLine(line))
                    {
                        sb.AppendLine(Content);
                        inserted = true;
                    }

                    sb.AppendLine(line);
                }

                // Degenerate case: the file is nothing but a preamble.
                if (!inserted) sb.AppendLine(Content);
            }

            return sb.ToString();
        }

        // Matched against a single line: blank, a // comment, or a #version /
        // #extension directive. Anything else is real code and marks the point
        // where injected content has to go in front.
        private static readonly Regex PreambleRegex =
            new Regex(@"^\s*(?:$|//|#\s*(?:version|extension)\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Blank lines, line comments and #version/#extension directives.</summary>
        private static bool IsPreambleLine(string line)
        {
            return PreambleRegex.IsMatch(line);
        }
    }
}
