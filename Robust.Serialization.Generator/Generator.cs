using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Robust.Serialization.Generator.Generators;
using static Robust.Serialization.Generator.CustomSerializerType;
using static Robust.Serialization.Generator.Types;

namespace Robust.Serialization.Generator;

[Generator]
public class Generator : IIncrementalGenerator
{
    private const string TypeCopierInterfaceNamespace = "Robust.Shared.Serialization.TypeSerializers.Interfaces.ITypeCopier";
    private const string TypeCopyCreatorInterfaceNamespace = "Robust.Shared.Serialization.TypeSerializers.Interfaces.ITypeCopyCreator";
    internal const string SerializationHooksNamespace = "Robust.Shared.Serialization.ISerializationHooks";

    public void Initialize(IncrementalGeneratorInitializationContext initContext)
    {
        IncrementalValuesProvider<TypeDeclarationSyntax> dataDefinitions = initContext.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is TypeDeclarationSyntax,
            static (context, _) =>
            {
                var type = (TypeDeclarationSyntax) context.Node;
                var symbol = (ITypeSymbol) context.SemanticModel.GetDeclaredSymbol(type)!;
                return IsDataDefinition(symbol) ? type : null;
            }
        ).Where(static type => type != null)!;

        var comparer = new DataDefinitionComparer();
        initContext.RegisterSourceOutput(
            initContext.CompilationProvider.Combine(dataDefinitions.WithComparer(comparer).Collect()),
            static (sourceContext, source) =>
            {
                var builder = new StringBuilder();
                var containingTypes = new Stack<INamedTypeSymbol>();
                var containingTypesStart = new StringBuilder();
                var containingTypesEnd = new StringBuilder();

                var (compilation, declarations) = source;
                foreach (var declaration in declarations)
                {
                    builder.Clear();
                    containingTypes.Clear();
                    containingTypesStart.Clear();
                    containingTypesEnd.Clear();

                    var type = compilation.GetSemanticModel(declaration.SyntaxTree).GetDeclaredSymbol(declaration)!;

                    var nonPartial = !IsPartial(declaration);

                    var namespaceString = type.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : $"namespace {type.ContainingNamespace.ToDisplayString()};";

                    var containingType = type.ContainingType;
                    while (containingType != null)
                    {
                        containingTypes.Push(containingType);
                        containingType = containingType.ContainingType;
                    }

                    foreach (var parent in containingTypes)
                    {
                        var syntax = (ClassDeclarationSyntax) parent.DeclaringSyntaxReferences[0].GetSyntax();
                        if (!IsPartial(syntax))
                        {
                            nonPartial = true;
                            continue;
                        }

                        containingTypesStart.AppendLine($"{GetPartialTypeDefinitionLine(parent)}\n{{");
                        containingTypesEnd.AppendLine("}");
                    }

                    var definition = GetDataDefinition(type);
                    if (nonPartial || definition.InvalidFields)
                        continue;


                    builder.AppendLine($$"""
                        #nullable enable
                        using System;
                        using Robust.Shared.Analyzers;
                        using Robust.Shared.IoC;
                        using Robust.Shared.GameObjects;
                        using Robust.Shared.Serialization;
                        using Robust.Shared.Serialization.Manager;
                        using Robust.Shared.Serialization.Manager.Exceptions;
                        using Robust.Shared.Serialization.TypeSerializers.Interfaces;
                        #pragma warning disable CS0618 // Type or member is obsolete
                        #pragma warning disable CS0612 // Type or member is obsolete
                        #pragma warning disable CS0108 // Member hides inherited member; missing new keyword
                        #pragma warning disable RA0002 // Robust access analyzer

                        {{namespaceString}}

                        {{containingTypesStart}}

                        {{GetPartialTypeDefinitionLine(type)}} : ISerializationGenerated<{{definition.GenericTypeName}}>
                        {
                        """);

                    builder.GenerateCopiers(definition, sourceContext, source);

                    builder.AppendLine($$"""
                        }

                        {{containingTypesEnd}}
                        """);

                    var symbolName = definition.Type
                        .ToDisplayString()
                        .Replace('<', '{')
                        .Replace('>', '}');

                    var sourceText = CSharpSyntaxTree
                        .ParseText(builder.ToString())
                        .GetRoot()
                        .NormalizeWhitespace()
                        .ToFullString();

                    sourceContext.AddSource($"{symbolName}.g.cs", sourceText);
                }
            }
        );
    }

    private static DataDefinition GetDataDefinition(ITypeSymbol definition)
    {
        var fields = new List<DataField>();
        var invalidFields = false;

        foreach (var member in definition.GetMembers())
        {
            if (member is not IFieldSymbol && member is not IPropertySymbol)
                continue;

            if (member.IsStatic)
                continue;

            if (IsDataField(member, out var type, out var attribute))
            {
                if (attribute.ConstructorArguments.FirstOrDefault(arg => arg.Kind == TypedConstantKind.Type).Value is INamedTypeSymbol customSerializer)
                {
                    if (ImplementsInterface(customSerializer, TypeCopierInterfaceNamespace))
                    {
                        fields.Add(new DataField(member, type, (customSerializer, Copier)));
                        continue;
                    }
                    else if (ImplementsInterface(customSerializer, TypeCopyCreatorInterfaceNamespace))
                    {
                        fields.Add(new DataField(member, type, (customSerializer, CopyCreator)));
                        continue;
                    }
                }

                fields.Add(new DataField(member, type, null));

                if (IsReadOnlyMember(definition, type))
                {
                    invalidFields = true;
                }
            }
        }

        var typeName = GetGenericTypeName(definition);
        var hasHooks = ImplementsInterface(definition, SerializationHooksNamespace);

        return new DataDefinition(definition, typeName, fields, hasHooks, invalidFields);
    }
}
