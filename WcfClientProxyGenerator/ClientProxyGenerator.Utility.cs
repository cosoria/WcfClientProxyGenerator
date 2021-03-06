﻿using Alphaleonis.Vsx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Editing;
using System.CodeDom.Compiler;
using System.Reflection;
using AlphaVSX.Roslyn;
using Microsoft.CodeAnalysis.CSharp;
using System.Threading;
using Microsoft.CodeAnalysis.Diagnostics;
using Alphaleonis.Vsx.Roslyn;
using Alphaleonis.Vsx.Roslyn.CSharp;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      #region Utility Methods

      private class OperationContractMethodInfo
      {
         public OperationContractMethodInfo(SemanticModel semanticModel, IMethodSymbol method)
         {
            var taskType = semanticModel.Compilation.RequireTypeByMetadataName(typeof(Task).FullName);
            var genericTaskType = semanticModel.Compilation.RequireTypeByMetadataName(typeof(Task<>).FullName);
            var operationContractAttributeType = semanticModel.Compilation.RequireTypeByMetadataName("System.ServiceModel.OperationContractAttribute");
            var faultContractAttributeType = semanticModel.Compilation.RequireTypeByMetadataName("System.ServiceModel.FaultContractAttribute");

            var operationContractAttribute = method.GetAttribute(operationContractAttributeType);
            if (operationContractAttribute == null)
               throw new InvalidOperationException($"The method {method.Name} is not decorated with {operationContractAttributeType.GetFullName()}.");

            OperationContractAttribute = new ModifiedOperationContractAttributeData(operationContractAttribute);

            FaultContractAttributes = method.GetAttributes().Where(attr => attr.AttributeClass.Equals(faultContractAttributeType)).ToImmutableArray();

            AdditionalAttributes = method.GetAttributes()
               .Where(
                  attr => !attr.AttributeClass.Equals(operationContractAttributeType) &&
                          !attr.AttributeClass.Equals(faultContractAttributeType)
               ).ToImmutableArray();

            IsAsync = method.ReturnType.Equals(taskType) || method.ReturnType.OriginalDefinition.Equals(genericTaskType.OriginalDefinition);

            ContractReturnsVoid = method.ReturnsVoid || method.ReturnType.Equals(taskType);

            Method = method;

            if (IsAsync && method.Name.EndsWith("Async"))
               ContractMethodName = method.Name.Substring(0, method.Name.Length - "Async".Length);
            else
               ContractMethodName = method.Name;

            if (IsAsync)
            {
               if (ContractReturnsVoid)
                  ContractReturnType = semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);
               else
                  ContractReturnType = ((INamedTypeSymbol)method.ReturnType).TypeArguments.Single();               
            }
            else
            {
               ContractReturnType = ReturnType;
            }

            AsyncReturnType = ContractReturnsVoid ? taskType : genericTaskType.Construct(ContractReturnType);
         }

         #region Properties

         public string MethodName { get { return Method.Name; } }

         public string ContractMethodName { get; }

         public bool ContractReturnsVoid { get; }

         public ITypeSymbol AsyncReturnType { get; }

         public ITypeSymbol ReturnType { get { return Method.ReturnType; } }

         public ITypeSymbol ContractReturnType { get; }

         public bool IsAsync { get; }
         
         public AttributeData OperationContractAttribute { get; }

         public ImmutableArray<AttributeData> AdditionalAttributes { get; }

         public ImmutableArray<AttributeData> FaultContractAttributes { get; }

         public ImmutableArray<IParameterSymbol> Parameters { get { return Method.Parameters; } }

         public IEnumerable<AttributeData> AllAttributes
         {
            get
            {
               return AdditionalAttributes.Concat(FaultContractAttributes).Concat(new[] { OperationContractAttribute });                              
            }
         }

         public IMethodSymbol Method { get; }

         #endregion

         #region Methods

         public bool ContractMacthes(OperationContractMethodInfo other)
         {
            return ContractMethodName.Equals(other.ContractMethodName) &&
               ContractReturnType.Equals(other.ContractReturnType) &&
               Parameters.Select(p => p.Type).SequenceEqual(other.Parameters.Select(p => p.Type));
         }

         #endregion

         private class ModifiedOperationContractAttributeData : AttributeData
         {
            private readonly AttributeData m_source;

            public ModifiedOperationContractAttributeData(AttributeData source)
            {
               m_source = source;
            }

            protected override SyntaxReference CommonApplicationSyntaxReference
            {
               get
               {
                  return m_source.ApplicationSyntaxReference;
               }
            }

            protected override INamedTypeSymbol CommonAttributeClass
            {
               get
               {
                  return m_source.AttributeClass;
               }
            }

            protected override IMethodSymbol CommonAttributeConstructor
            {
               get
               {
                  return m_source.AttributeConstructor;
               }
            }

            protected override ImmutableArray<TypedConstant> CommonConstructorArguments
            {
               get
               {
                  return m_source.ConstructorArguments;
               }
            }


            protected override ImmutableArray<KeyValuePair<string, TypedConstant>> CommonNamedArguments
            {
               get
               {
                  return m_source.NamedArguments.Where(arg => !arg.Key.Equals("AsyncPattern")).ToImmutableArray();
               }
            }
         }
      }

      private ImmutableArray<OperationContractMethodInfo> GetOperationContractMethodInfos(SemanticModel semanticModel, INamedTypeSymbol serviceInterface)
      {
         ITypeSymbol operationContractAttributeType = semanticModel.Compilation.RequireTypeByMetadataName("System.ServiceModel.OperationContractAttribute");

         var arrayBuilder = ImmutableArray.CreateBuilder<OperationContractMethodInfo>();
         foreach (IMethodSymbol sourceMethod in serviceInterface.GetAllMembers().OfType<IMethodSymbol>().Where(m => m.GetAttribute(operationContractAttributeType) != null))
         {
            arrayBuilder.Add(new OperationContractMethodInfo(semanticModel, sourceMethod));

         }

         return arrayBuilder.ToImmutable();         
      }

      private ImmutableList<MethodDeclarationSyntax> GetOperationContractMethodDeclarations(SemanticModel semanticModel, SyntaxGenerator gen, INamedTypeSymbol serviceInterface, bool includeAttributes, bool includeSourceInterfaceMethods, bool excludeAsyncMethods)
      {
         ImmutableList<MethodDeclarationSyntax> methods = ImmutableList<MethodDeclarationSyntax>.Empty;

         var sourceMethods = GetOperationContractMethodInfos(semanticModel, serviceInterface);

         foreach (var methodInfo in sourceMethods.OrderBy(m => m.ContractMethodName).ThenBy(m => m.IsAsync))
         {            
            // Emit non-async version of method 
            if (!methodInfo.IsAsync || !sourceMethods.Any(m => m.ContractMacthes(methodInfo) && !m.IsAsync))
            {
               SyntaxNode targetMethod = gen.MethodDeclaration(methodInfo.Method);
               targetMethod = gen.WithType(targetMethod, gen.TypeExpression(methodInfo.ContractReturnType));
               targetMethod = gen.WithName(targetMethod, methodInfo.ContractMethodName);

               if (includeAttributes)
               {
                  var extraFaultContractAttributes = sourceMethods.FirstOrDefault(m => m.ContractMacthes(methodInfo) && m.IsAsync != methodInfo.IsAsync)?.FaultContractAttributes ?? Enumerable.Empty<AttributeData>();
                  targetMethod = gen.AddAttributes(targetMethod, methodInfo.AllAttributes.Concat(extraFaultContractAttributes).Select(a => gen.Attribute(a)));
               }

               targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
               methods = methods.Add((MethodDeclarationSyntax)targetMethod);
            }

            // Emit async-version of method
            if (!excludeAsyncMethods)
            {
               if (methodInfo.IsAsync || !sourceMethods.Any(m => m.ContractMacthes(methodInfo) && m.IsAsync))
               {
                  SyntaxNode targetMethod = gen.MethodDeclaration(methodInfo.Method);
                  targetMethod = gen.WithType(targetMethod, gen.TypeExpression(methodInfo.AsyncReturnType));
                  targetMethod = gen.WithName(targetMethod, methodInfo.ContractMethodName + "Async");

                  if (includeAttributes)
                  {
                     targetMethod = gen.AddAttributes(targetMethod, methodInfo.AdditionalAttributes.Select(a => gen.Attribute(a)));
                     targetMethod = gen.AddAttributes(targetMethod,
                        gen.AddAttributeArguments(gen.Attribute(methodInfo.OperationContractAttribute), new[] { gen.AttributeArgument("AsyncPattern", gen.TrueLiteralExpression()) })
                     );
                  }

                  targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
                  methods = methods.Add((MethodDeclarationSyntax)targetMethod);
               }
            }
         }
         
         return methods;
      }      
                 

      private bool IsGenericTaskType(Compilation compilation, INamedTypeSymbol namedReturnType)
      {
         return namedReturnType.IsGenericType && namedReturnType.ConstructUnboundGenericType().Equals(compilation.RequireType(typeof(Task<>)).ConstructUnboundGenericType());
      }
     
      private T AddGeneratedCodeAttribute<T>(SyntaxGenerator g, T node) where T : SyntaxNode
      {
         return (T)g.AddAttributes(node,
            g.Attribute("System.CodeDom.Compiler.GeneratedCodeAttribute",
               g.LiteralExpression(GetCodeGeneratorName()),
               g.LiteralExpression(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>().Version)
            )
         );
      }

      private static string GetCodeGeneratorName()
      {
         return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>().Title;
      }

      private IEnumerable<IMethodSymbol> GetOperationContractMethods(Compilation compilation, INamedTypeSymbol proxyInterface)
      {
         return proxyInterface.GetAllMembers().OfType<IMethodSymbol>().Where(member => member.GetAttributes().Any(attr => attr.AttributeClass.Equals(GetOperationContractAttributeType(compilation))));
      }

      private bool ReturnsTask(Compilation compilation, IMethodSymbol sourceMethod)
      {
         return sourceMethod.ReturnType.Equals(compilation.RequireType<Task>()) || IsGenericTaskType(compilation, ((INamedTypeSymbol)sourceMethod.ReturnType));
      }

      private bool IsVoid(Compilation compilation, IMethodSymbol sourceMethod)
      {
         return sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(compilation.RequireType<Task>());            
      }

      private SyntaxNode AwaitExpressionIfAsync(SyntaxGenerator g, bool isAsync, SyntaxNode expression, bool configureAwait = false)
      {
         if (isAsync)
            return AwaitExpression(g, expression, configureAwait);
         else
            return expression;
      }

      private SyntaxNode AwaitExpression(SyntaxGenerator g, SyntaxNode expression, bool configureAwait = false)
      {
         return g.AwaitExpression(
            g.InvocationExpression(
               g.MemberAccessExpression(
                  expression,
                  "ConfigureAwait"
               ),
               g.LiteralExpression(configureAwait)
            )
         );
      }

      

      #endregion
   }
}