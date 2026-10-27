using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MediaKernel.Generators
{
    /// <summary>
    /// Source generator that discovers classes marked with [Codec] attribute
    /// and generates an AOT-compatible static registry.
    /// </summary>
    [Generator]
    public class CodecRegistryGenerator : IIncrementalGenerator
    {
        private const string CodecAttributeName = "MediaKernel.Core.Codecs.CodecAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Find all classes with [Codec] attribute
            var codecClasses = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateClass(s),
                    transform: static (ctx, _) => GetCodecInfo(ctx))
                .Where(static m => m is not null);

            // Combine with compilation
            var compilationAndClasses = context.CompilationProvider.Combine(codecClasses.Collect());

            // Generate the registry source
            context.RegisterSourceOutput(compilationAndClasses, 
                static (spc, source) => Execute(source.Left, source.Right, spc));
        }

        private static bool IsCandidateClass(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classDecl && 
                   classDecl.AttributeLists.Count > 0;
        }

        private static CodecInfo? GetCodecInfo(GeneratorSyntaxContext context)
        {
            var classDecl = (ClassDeclarationSyntax)context.Node;
            
            foreach (var attributeList in classDecl.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var symbol = context.SemanticModel.GetSymbolInfo(attribute).Symbol;
                    if (symbol is not IMethodSymbol attributeSymbol)
                        continue;

                    var attributeType = attributeSymbol.ContainingType;
                    if (attributeType.ToDisplayString() != CodecAttributeName)
                        continue;

                    // Get the class symbol
                    var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl);
                    if (classSymbol is null)
                        continue;

                    // Parse attribute arguments
                    var codecId = "";
                    var codecType = "Video";
                    var priority = 10;
                    var displayName = (string?)null;
                    var isHardwareAccelerated = false;

                    if (attribute.ArgumentList is not null)
                    {
                        var args = attribute.ArgumentList.Arguments;
                        
                        // First positional arg: codecId
                        if (args.Count > 0)
                        {
                            var codecIdExpr = args[0].Expression;
                            var constantValue = context.SemanticModel.GetConstantValue(codecIdExpr);
                            if (constantValue.HasValue)
                                codecId = constantValue.Value?.ToString() ?? "";
                        }

                        // Second positional arg: type
                        if (args.Count > 1)
                        {
                            var typeExpr = args[1].Expression;
                            if (typeExpr is MemberAccessExpressionSyntax memberAccess)
                            {
                                codecType = memberAccess.Name.Identifier.Text;
                            }
                        }

                        // Named arguments
                        foreach (var arg in args)
                        {
                            if (arg.NameEquals is null)
                                continue;

                            var name = arg.NameEquals.Name.Identifier.Text;
                            var value = context.SemanticModel.GetConstantValue(arg.Expression);

                            switch (name)
                            {
                                case "Priority":
                                    if (value.HasValue && value.Value is int p)
                                        priority = p;
                                    break;
                                case "DisplayName":
                                    if (value.HasValue)
                                        displayName = value.Value?.ToString();
                                    break;
                                case "IsHardwareAccelerated":
                                    if (value.HasValue && value.Value is bool b)
                                        isHardwareAccelerated = b;
                                    break;
                            }
                        }
                    }

                    return new CodecInfo(
                        classSymbol.ToDisplayString(),
                        classSymbol.Name,
                        codecId,
                        codecType,
                        priority,
                        displayName,
                        isHardwareAccelerated);
                }
            }

            return null;
        }

        private static void Execute(
            Compilation compilation, 
            IEnumerable<CodecInfo?> codecs, 
            SourceProductionContext context)
        {
            var codecList = codecs.Where(c => c is not null).Cast<CodecInfo>().ToList();

            if (codecList.Count == 0)
            {
                // Generate empty registry
                context.AddSource("CodecRegistry.g.cs", SourceText.From(GenerateEmptyRegistry(), Encoding.UTF8));
                return;
            }

            var source = GenerateRegistry(codecList);
            context.AddSource("CodecRegistry.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        private static string GenerateEmptyRegistry()
        {
            return @"// <auto-generated/>
#nullable enable

namespace MediaKernel.Core.Codecs
{
    /// <summary>
    /// Auto-generated codec registry. Provides AOT-compatible codec discovery.
    /// </summary>
    public static partial class CodecRegistry
    {
        private static readonly CodecRegistration[] _registrations = System.Array.Empty<CodecRegistration>();

        /// <summary>Gets all registered codecs.</summary>
        public static System.ReadOnlySpan<CodecRegistration> AllCodecs => _registrations;

        /// <summary>Gets codecs matching the specified codec ID, ordered by priority.</summary>
        public static System.Collections.Generic.IEnumerable<CodecRegistration> GetCodecs(string codecId)
        {
            return System.Array.Empty<CodecRegistration>();
        }

        /// <summary>Gets the best codec for the specified codec ID (highest priority).</summary>
        public static CodecRegistration? GetBestCodec(string codecId)
        {
            return null;
        }

        /// <summary>Creates a decoder for the specified codec ID.</summary>
        public static IDecoder? CreateDecoder(string codecId)
        {
            return null;
        }
    }
}
";
        }

        private static string GenerateRegistry(List<CodecInfo> codecs)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("namespace MediaKernel.Core.Codecs");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Auto-generated codec registry. Provides AOT-compatible codec discovery.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static partial class CodecRegistry");
            sb.AppendLine("    {");

            // Generate static array of registrations
            sb.AppendLine("        private static readonly CodecRegistration[] _registrations = new CodecRegistration[]");
            sb.AppendLine("        {");

            foreach (var codec in codecs.OrderByDescending(c => c.Priority))
            {
                var displayNameArg = codec.DisplayName is null 
                    ? "null" 
                    : $"\"{EscapeString(codec.DisplayName)}\"";

                sb.AppendLine($"            new CodecRegistration(");
                sb.AppendLine($"                \"{EscapeString(codec.CodecId)}\",");
                sb.AppendLine($"                CodecType.{codec.CodecType},");
                sb.AppendLine($"                {codec.Priority},");
                sb.AppendLine($"                {displayNameArg},");
                sb.AppendLine($"                {codec.IsHardwareAccelerated.ToString().ToLowerInvariant()},");
                sb.AppendLine($"                () => new {codec.FullTypeName}()),");
            }

            sb.AppendLine("        };");
            sb.AppendLine();

            // AllCodecs property
            sb.AppendLine("        /// <summary>Gets all registered codecs.</summary>");
            sb.AppendLine("        public static System.ReadOnlySpan<CodecRegistration> AllCodecs => _registrations;");
            sb.AppendLine();

            // GetCodecs method
            sb.AppendLine("        /// <summary>Gets codecs matching the specified codec ID, ordered by priority.</summary>");
            sb.AppendLine("        public static System.Collections.Generic.IEnumerable<CodecRegistration> GetCodecs(string codecId)");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int i = 0; i < _registrations.Length; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (string.Equals(_registrations[i].CodecId, codecId, System.StringComparison.OrdinalIgnoreCase))");
            sb.AppendLine("                {");
            sb.AppendLine("                    yield return _registrations[i];");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();

            // GetBestCodec method
            sb.AppendLine("        /// <summary>Gets the best codec for the specified codec ID (highest priority).</summary>");
            sb.AppendLine("        public static CodecRegistration? GetBestCodec(string codecId)");
            sb.AppendLine("        {");
            sb.AppendLine("            CodecRegistration? best = null;");
            sb.AppendLine("            int bestPriority = int.MinValue;");
            sb.AppendLine("            for (int i = 0; i < _registrations.Length; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (string.Equals(_registrations[i].CodecId, codecId, System.StringComparison.OrdinalIgnoreCase)");
            sb.AppendLine("                    && _registrations[i].Priority > bestPriority)");
            sb.AppendLine("                {");
            sb.AppendLine("                    best = _registrations[i];");
            sb.AppendLine("                    bestPriority = _registrations[i].Priority;");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return best;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // CreateDecoder method
            sb.AppendLine("        /// <summary>Creates a decoder for the specified codec ID.</summary>");
            sb.AppendLine("        public static IDecoder? CreateDecoder(string codecId)");
            sb.AppendLine("        {");
            sb.AppendLine("            var best = GetBestCodec(codecId);");
            sb.AppendLine("            return best?.Factory();");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private class CodecInfo
        {
            public string FullTypeName { get; }
            public string ClassName { get; }
            public string CodecId { get; }
            public string CodecType { get; }
            public int Priority { get; }
            public string? DisplayName { get; }
            public bool IsHardwareAccelerated { get; }

            public CodecInfo(
                string fullTypeName, string className, string codecId, 
                string codecType, int priority, string? displayName, 
                bool isHardwareAccelerated)
            {
                FullTypeName = fullTypeName;
                ClassName = className;
                CodecId = codecId;
                CodecType = codecType;
                Priority = priority;
                DisplayName = displayName;
                IsHardwareAccelerated = isHardwareAccelerated;
            }
        }
    }
}
