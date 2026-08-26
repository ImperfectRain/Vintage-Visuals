using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VintageVisuals.Common.Patching
{
    public sealed class ShaderPatchOperationRecord
    {
        public string Group;
        public string Kind;
        public string Phase;
        public string Origin;
        public string Anchor;
        public int Matches;
    }

    public sealed class ShaderPatchManifestEntry
    {
        public string Filename;
        public string BaseSourceHash;
        public string FinalSourceHash;
        public readonly List<ShaderPatchOperationRecord> Operations = new List<ShaderPatchOperationRecord>();
        public readonly List<string> DeclarationsBefore = new List<string>();
        public readonly List<string> DeclarationsAfter = new List<string>();
        public readonly List<string> SamplersBefore = new List<string>();
        public readonly List<string> SamplersAfter = new List<string>();
        public readonly List<string> VaryingsBefore = new List<string>();
        public readonly List<string> VaryingsAfter = new List<string>();
        public readonly List<string> DeclarationsAdded = new List<string>();
        public readonly List<string> DeclarationsRemoved = new List<string>();
        public readonly List<string> SamplersAdded = new List<string>();
        public readonly List<string> SamplersRemoved = new List<string>();
        public readonly List<string> VaryingsAdded = new List<string>();
        public readonly List<string> VaryingsRemoved = new List<string>();
        public bool ValidationPassed;
        public string ValidationMessage;

        public string ToStableText()
        {
            var sb = new StringBuilder();
            Line(sb, "file", Filename);
            Line(sb, "baseSha256", BaseSourceHash);
            Line(sb, "finalSha256", FinalSourceHash);
            Line(sb, "validation", ValidationPassed ? "PASS" : "FAIL");
            if (!string.IsNullOrEmpty(ValidationMessage)) Line(sb, "validationMessage", ValidationMessage);

            sb.Append("operations:\n");
            foreach (ShaderPatchOperationRecord op in Operations)
            {
                sb.Append("  - group=").Append(op.Group)
                  .Append(" phase=").Append(op.Phase)
                  .Append(" kind=").Append(op.Kind)
                  .Append(" matches=").Append(op.Matches)
                  .Append(" anchor=").Append(op.Anchor)
                  .Append(" origin=").Append(op.Origin)
                  .Append('\n');
            }

            Section(sb, "declarationsBefore", DeclarationsBefore);
            Section(sb, "declarationsAfter", DeclarationsAfter);
            Section(sb, "declarationsAdded", DeclarationsAdded);
            Section(sb, "declarationsRemoved", DeclarationsRemoved);
            Section(sb, "samplersBefore", SamplersBefore);
            Section(sb, "samplersAfter", SamplersAfter);
            Section(sb, "samplersAdded", SamplersAdded);
            Section(sb, "samplersRemoved", SamplersRemoved);
            Section(sb, "varyingsBefore", VaryingsBefore);
            Section(sb, "varyingsAfter", VaryingsAfter);
            Section(sb, "varyingsAdded", VaryingsAdded);
            Section(sb, "varyingsRemoved", VaryingsRemoved);
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append(": ").Append(value ?? "").Append('\n');
        }

        private static void Section(StringBuilder sb, string name, IEnumerable<string> values)
        {
            sb.Append(name).Append(":\n");
            foreach (string value in values.OrderBy(v => v, StringComparer.Ordinal))
            {
                sb.Append("  - ").Append(value).Append('\n');
            }
        }
    }
}
