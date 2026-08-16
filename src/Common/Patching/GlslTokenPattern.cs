using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// Turns a literal snippet of GLSL into a whitespace-insensitive regex.
    ///
    /// Why this exists: patches anchor on vanilla shader source, and vanilla
    /// reformats freely between game versions. A patch written against
    ///     <c>outGlow = vec4(glowLevel, 0, 0, color.a);</c>
    /// should still match after the line is rewrapped as
    ///     <c>outGlow = vec4( glowLevel , 0, 0, color.a );</c>
    /// Writing that tolerance by hand in every patch file is unreadable, so
    /// patch authors write the GLSL as it appears today and this builds the
    /// forgiving pattern.
    ///
    /// What it deliberately does NOT tolerate: renamed identifiers, reordered
    /// arguments, or changed literals. Those are real semantic changes and
    /// should fail loudly so someone re-reads the vanilla shader.
    /// </summary>
    public static class GlslTokenPattern
    {
        /// <summary>Characters that make up an identifier or number in GLSL.</summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// Builds a regex matching <paramref name="tokens"/> ignoring how much
        /// whitespace separates the individual tokens.
        /// </summary>
        public static Regex Build(string tokens)
        {
            List<string> atoms = Tokenize(tokens);

            if (atoms.Count == 0)
            {
                // An empty token string would compile to a pattern matching the
                // empty string at every position — catastrophic as a patch.
                throw new System.ArgumentException("Token pattern is empty", nameof(tokens));
            }

            var sb = new StringBuilder();

            // Identifier boundaries are lookarounds rather than consumed
            // characters, so the match is exactly the token text and the
            // replacement does not have to re-emit the surrounding delimiters.
            bool firstIsWord = IsWordChar(atoms[0][0]);
            if (firstIsWord) sb.Append(@"(?<![A-Za-z0-9_])");

            for (int i = 0; i < atoms.Count; i++)
            {
                if (i > 0)
                {
                    // Two adjacent identifiers must stay separated ("void main"
                    // must not match "voidmain"). Anything involving a symbol
                    // may have its whitespace freely added or removed.
                    bool bothWords = IsWordChar(atoms[i - 1][0]) && IsWordChar(atoms[i][0]);
                    sb.Append(bothWords ? @"\s+" : @"\s*");
                }

                sb.Append(Regex.Escape(atoms[i]));
            }

            bool lastIsWord = IsWordChar(atoms[atoms.Count - 1][0]);
            if (lastIsWord) sb.Append(@"(?![A-Za-z0-9_])");

            return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Splits GLSL into runs of identifier characters and single symbol
        /// characters, dropping whitespace entirely. "color.a" becomes
        /// ["color", ".", "a"]; "==" becomes ["=", "="].
        /// </summary>
        private static List<string> Tokenize(string source)
        {
            var atoms = new List<string>();
            int i = 0;

            while (i < source.Length)
            {
                char c = source[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (IsWordChar(c))
                {
                    int start = i;
                    while (i < source.Length && IsWordChar(source[i])) i++;
                    atoms.Add(source.Substring(start, i - start));
                }
                else
                {
                    atoms.Add(c.ToString());
                    i++;
                }
            }

            return atoms;
        }
    }
}
