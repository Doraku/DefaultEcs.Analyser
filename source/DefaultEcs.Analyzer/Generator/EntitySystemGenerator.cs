﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using DefaultEcs.Analyzer.Extension;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DefaultEcs.Analyzer
{
    [Generator]
    public sealed class EntitySystemGenerator : ISourceGenerator
    {
        private static Compilation GenerateAttributes(GeneratorExecutionContext context)
        {
            const string attributesSource =
@"
using System;
using System.Runtime.CompilerServices;

namespace DefaultEcs.System
{
    /// <summary>
    /// Makes so that the decorated method will be called by the current system type when no Update method are overridden.
    /// </summary>
    [CompilerGenerated, AttributeUsage(AttributeTargets.Method)]
    internal sealed class UpdateAttribute : Attribute
    { }
}";

            context.AddSource("Attributes", attributesSource);

            return context.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                SourceText.From(attributesSource, Encoding.UTF8),
                (context.Compilation as CSharpCompilation)?.SyntaxTrees[0].Options as CSharpParseOptions));
        }

        public void Initialize(GeneratorInitializationContext context)
        { }

        public void Execute(GeneratorExecutionContext context)
        {
            Compilation compilation = GenerateAttributes(context);
            int systemCount = 0;

            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                SemanticModel semanticModel = compilation.GetSemanticModel(tree);

                StringBuilder code = new StringBuilder();
                foreach (IMethodSymbol method in tree
                    .GetRoot()
                    .DescendantNodesAndSelf()
                    .OfType<MethodDeclarationSyntax>()
                    .Select(m => semanticModel.GetDeclaredSymbol(m))
                    .Where(m => m.HasUpdateAttribute()
                        && m.ContainingType.IsPartial()
                        && m.ContainingType.IsEntitySystem()
                        && m.ContainingType.GetMembers().OfType<IMethodSymbol>().Count(m => m.HasUpdateAttribute()) is 1
                        && !m.ContainingType.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsEntitySystemUpdateOverride())
                        && !m.Parameters.Any(p => p.RefKind == RefKind.Out)))
                {
                    INamedTypeSymbol type = method.ContainingType;
                    code.Clear();

                    List<string> updateOverrideParameters = new();
                    List<string> parameters = new();
                    List<string> components = new();
                    List<string> withAttributes = new();

                    bool isBufferType = false;
                    if (type.IsAEntitySystem(out IList<ITypeSymbol> genericTypes))
                    {
                        updateOverrideParameters.Add($"{genericTypes[0]} state");
                    }
                    else if (type.IsAEntitiesSystem(out genericTypes))
                    {
                        updateOverrideParameters.Add($"{genericTypes[0]} state");
                        updateOverrideParameters.Add($"in {genericTypes[1]} key");
                    }
                    else if (type.IsAEntityBufferedSystem(out genericTypes))
                    {
                        updateOverrideParameters.Add($"{genericTypes[0]} state");
                        isBufferType = true;
                    }
                    else if (type.IsAEntitiesBufferedSystem(out genericTypes))
                    {
                        updateOverrideParameters.Add($"{genericTypes[0]} state");
                        updateOverrideParameters.Add($"in {genericTypes[1]} key");
                        isBufferType = true;
                    }

                    foreach (IParameterSymbol parameter in method.Parameters)
                    {
                        if (parameter.Type.IsEntity() && parameter.RefKind != RefKind.Ref)
                        {
                            parameters.Add("entity");
                        }
                        else if (SymbolEqualityComparer.Default.Equals(parameter.Type, genericTypes[0]) && parameter.RefKind == RefKind.None)
                        {
                            parameters.Add("state");
                        }
                        else if (genericTypes.Count > 1 && SymbolEqualityComparer.Default.Equals(parameter.Type, genericTypes[1]) && parameter.RefKind != RefKind.Ref)
                        {
                            parameters.Add("key");
                        }
                        else if (parameter.Type.IsComponents() && parameter.Type is INamedTypeSymbol componentType)
                        {
                            string name = $"components{components.Count}";

                            components.Add($"            {parameter.Type} {name} = World.GetComponents<global::{componentType.TypeArguments[0]}>();");
                            parameters.Add((parameter.RefKind == RefKind.Ref ? "ref " : string.Empty) + name);
                        }
                        else
                        {
                            string name = $"components{components.Count}";

                            withAttributes.Add($"typeof(global::{parameter.Type})");
                            components.Add($"            Components<global::{parameter.Type}> {name} = World.GetComponents<global::{parameter.Type}>();");
                            parameters.Add($"{(parameter.RefKind == RefKind.Ref ? "ref " : string.Empty)}{name}[entity]");
                        }
                    }

                    List<INamedTypeSymbol> parentTypes = type.GetParentTypes().Skip(1).Reverse().ToList();

                    code.AppendLine("using System;");
                    code.AppendLine("using System.Collections.Generic;");
                    code.AppendLine("using System.Runtime.CompilerServices;");
                    code.AppendLine("using DefaultEcs;");
                    code.AppendLine("using DefaultEcs.System;");
                    code.AppendLine("using DefaultEcs.Threading;");
                    code.AppendLine();
                    code.Append("namespace ").AppendLine(type.GetNamespace());
                    code.AppendLine("{");

                    foreach (INamedTypeSymbol parentType in parentTypes)
                    {
                        code.Append("    ").Append(parentType.DeclaredAccessibility.ToCode()).Append(" partial ").Append(parentType.TypeKind.ToCode()).Append(' ').AppendLine(parentType.Name);
                        code.AppendLine("    {");
                    }

                    code.Append("    [With(").Append(string.Join(", ", withAttributes)).AppendLine(")]");
                    code.Append("    ").Append(type.DeclaredAccessibility.ToCode()).Append(" partial class ").AppendLine(type.Name);
                    code.AppendLine("    {");

                    if (type.Constructors.All(c => c.IsImplicitlyDeclared))
                    {
                        if (isBufferType)
                        {
                            code.AppendLine("        [CompilerGenerated]");
                            code.Append("        public ").Append(type.Name).AppendLine("(World world)");
                            code.AppendLine("            : base(world)");
                            code.AppendLine("        { }");
                        }
                        else
                        {
                            code.AppendLine("        [CompilerGenerated]");
                            code.Append("        public ").Append(type.Name).AppendLine("(World world, IParallelRunner runner, int minEntityCountByRunnerIndex)");
                            code.AppendLine("            : base(world, runner, minEntityCountByRunnerIndex)");
                            code.AppendLine("        { }");
                            code.AppendLine();
                            code.AppendLine("        [CompilerGenerated]");
                            code.Append("        public ").Append(type.Name).AppendLine("(World world, IParallelRunner runner)");
                            code.AppendLine("            : this(world, runner, 0)");
                            code.AppendLine("        { }");
                            code.AppendLine();
                            code.AppendLine("        [CompilerGenerated]");
                            code.Append("        public ").Append(type.Name).AppendLine("(World world)");
                            code.AppendLine("            : this(world, null, 0)");
                            code.AppendLine("        { }");
                            code.AppendLine();
                        }
                        code.AppendLine();
                    }

                    code.AppendLine("        [CompilerGenerated]");
                    code.Append("        protected override void Update(").Append(string.Join(", ", updateOverrideParameters)).AppendLine(", ReadOnlySpan<Entity> entities)");
                    code.AppendLine("        {");

                    foreach (string component in components)
                    {
                        code.AppendLine(component);
                    }

                    code.AppendLine("            foreach (ref readonly Entity entity in entities)");
                    code.AppendLine("            {");
                    code.Append("                ").Append(method.Name).Append('(').Append(string.Join(", ", parameters)).AppendLine(");");
                    code.AppendLine("            }");
                    code.AppendLine("        }");
                    code.AppendLine("    }");

                    for (int i = 0; i < parentTypes.Count; ++i)
                    {
                        code.AppendLine("    }");
                    }

                    code.AppendLine("}");

                    context.AddSource($"System{++systemCount}", code.ToString());
                }
            }
        }
    }
}