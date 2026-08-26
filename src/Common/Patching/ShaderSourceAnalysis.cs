using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VintageVisuals.Common.Patching
{
    public sealed class ShaderSourceFacts
    {
        public List<string> Declarations { get; } = new List<string>();
        public List<string> Samplers { get; } = new List<string>();
        public List<string> Varyings { get; } = new List<string>();
    }

    public static class ShaderSourceAnalysis
    {
        private static readonly Regex DeclarationRegex = new Regex(
            @"^\s*(?:layout\s*\([^)]*\)\s*)?(uniform|in|out|varying)\s+([^;]+;)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex SamplerRegex = new Regex(
            @"\buniform\s+(sampler\w+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static string NormalizeNewlines(string source)
        {
            if (source == null) return "";
            return source.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        public static string Sha256(string source)
        {
            string normalized = NormalizeNewlines(source);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static ShaderSourceFacts Facts(string source)
        {
            var facts = new ShaderSourceFacts();
            string normalized = NormalizeNewlines(source);

            foreach (Match match in DeclarationRegex.Matches(normalized))
            {
                string declaration = Collapse(match.Value);
                facts.Declarations.Add(declaration);

                string qualifier = match.Groups[1].Value;
                string body = match.Groups[2].Value;
                if (qualifier == "in" || qualifier == "out" || qualifier == "varying")
                {
                    facts.Varyings.Add(Collapse(qualifier + " " + body));
                }
            }

            foreach (Match match in SamplerRegex.Matches(normalized))
            {
                facts.Samplers.Add(match.Groups[1].Value + " " + match.Groups[2].Value);
            }

            facts.Declarations.Sort(StringComparer.Ordinal);
            facts.Samplers.Sort(StringComparer.Ordinal);
            facts.Varyings.Sort(StringComparer.Ordinal);
            return facts;
        }

        public static List<string> Added(IEnumerable<string> before, IEnumerable<string> after)
        {
            return after.Except(before, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        public static List<string> Removed(IEnumerable<string> before, IEnumerable<string> after)
        {
            return before.Except(after, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        private static string Collapse(string text)
        {
            return Regex.Replace(text ?? "", @"\s+", " ").Trim();
        }
    }
}
