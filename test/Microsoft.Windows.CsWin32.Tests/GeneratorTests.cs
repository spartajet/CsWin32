﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Windows.CsWin32;
using Xunit;
using Xunit.Abstractions;

public class GeneratorTests : IDisposable, IAsyncLifetime
{
    private static readonly string FileSeparator = new string('=', 140);
    private readonly ITestOutputHelper logger;
    private readonly FileStream metadataStream;
    private CSharpCompilation compilation;
    private CSharpParseOptions parseOptions;
    private Generator? generator;

    public GeneratorTests(ITestOutputHelper logger)
    {
        this.logger = logger;
        this.metadataStream = File.OpenRead(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location!)!, "Windows.Win32.winmd"));

        this.parseOptions = CSharpParseOptions.Default
            .WithDocumentationMode(DocumentationMode.Diagnose)
            .WithLanguageVersion(LanguageVersion.CSharp9);
        this.compilation = null!; // set in InitializeAsync
    }

    public async Task InitializeAsync()
    {
        ReferenceAssemblies references = ReferenceAssemblies.NetStandard.NetStandard20
            .AddPackages(ImmutableArray.Create(
                new PackageIdentity("System.Memory", "4.5.4"),
                new PackageIdentity("Microsoft.Windows.SDK.Contracts", "10.0.19041.1")));
        ImmutableArray<MetadataReference> metadataReferences = await references.ResolveAsync(LanguageNames.CSharp, default);

        // Workaround for https://github.com/dotnet/roslyn-sdk/issues/699
        metadataReferences = metadataReferences.AddRange(
            Directory.GetFiles(Path.Combine(Path.GetTempPath(), "test-packages", "Microsoft.Windows.SDK.Contracts.10.0.19041.1", "ref", "netstandard2.0"), "*.winmd").Select(p => MetadataReference.CreateFromFile(p)));

        // CONSIDER: How can I pass in the source generator itself, with AdditionalFiles, so I'm exercising that code too?
        this.compilation = CSharpCompilation.Create(
            assemblyName: "test",
            references: metadataReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        this.generator?.Dispose();
        this.metadataStream.Dispose();
    }

    [Fact]
    public void SimplestMethod()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerateExternMethod("GetTickCount"));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    [Theory]
    [InlineData("CreateFile")] // SafeHandle-derived type
    [InlineData("D3DGetTraceInstructionOffsets")] // SizeParamIndex
    [InlineData("PlgBlt")] // SizeConst
    [InlineData("ID3D12Resource")] // COM interface with base types
    [InlineData("ID2D1RectangleGeometry")] // COM interface with base types
    [InlineData("ENABLE_TRACE_PARAMETERS_V1")] // bad xml created at some point.
    [InlineData("JsRuntimeVersion")] // An enum that has an extra member in a separate header file.
    [InlineData("ReportEvent")] // Failed at one point
    [InlineData("DISPLAYCONFIG_VIDEO_SIGNAL_INFO")] // Union, explicit layout, bitmask, nested structs
    [InlineData("g_wszStreamBufferRecordingDuration")] // Constant string field
    [InlineData("MFVideoAlphaBitmap")] // field named params
    [InlineData("DDRAWI_DDVIDEOPORT_INT")] // field that is never used
    [InlineData("MainAVIHeader")] // dwReserved field is a fixed length array
    [InlineData("JsRuntimeVersionEdge")] // Constant typed as an enum
    [InlineData("POSITIVE_INFINITY")] // Special float imaginary number
    [InlineData("NEGATIVE_INFINITY")] // Special float imaginary number
    [InlineData("NaN")] // Special float imaginary number
    [InlineData("HBMMENU_POPUP_RESTORE")] // A HBITMAP handle as a constant
    [InlineData("RpcServerRegisterIfEx")] // Optional attribute on delegate type.
    [InlineData("RpcSsSwapClientAllocFree")] // Parameters typed as pointers to in delegates and out delegates
    [InlineData("RPC_DISPATCH_TABLE")] // Struct with a field typed as a delegate
    [InlineData("RPC_SERVER_INTERFACE")] // Struct with a field typed as struct with a field typed as a delegate
    [InlineData("DDHAL_DESTROYDRIVERDATA")] // Struct with a field typed as a delegate
    [InlineData("I_RpcServerInqAddressChangeFn")] // p/invoke that returns a function pointer
    [InlineData("WSPUPCALLTABLE")] // a delegate with a delegate in its signature
    [InlineData("HWND_BOTTOM")] // A constant typed as a typedef'd struct
    [InlineData("BOOL")] // a special cased typedef struct
    [InlineData("uregex_getMatchCallback")] // friendly overload with delegate parameter, and out parameters
    [InlineData("CreateDispatcherQueueController")] // References a WinRT type
    [InlineData("RegOpenKey")] // allocates a handle with a release function that returns LSTATUS
    [InlineData("LsaRegisterLogonProcess")] // allocates a handle with a release function that returns NTSTATUS
    [InlineData("FilterCreate")] // allocates a handle with a release function that returns HRESULT
    [InlineData("DsGetDcOpen")] // allocates a handle with a release function that returns HRESULT
    [InlineData("DXVAHDSW_CALLBACKS")] // pointers to handles
    [InlineData("HBITMAP_UserMarshal")] // in+out handle pointer
    public void InterestingAPIs(string api)
    {
        this.generator = new Generator(this.metadataStream, options: new GeneratorOptions { EmitSingleFile = true }, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate(api, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    /// <summary>
    /// Verifies that GetLastError is never generated.
    /// Users should call <see cref="Marshal.GetLastWin32Error"/> instead.
    /// </summary>
    [Fact]
    public void GetLastErrorNotIncludedInBulkGeneration()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("kernel32.*", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        Assert.True(this.IsMethodGenerated("CreateFile"));
        Assert.False(this.IsMethodGenerated("GetLastError"));
    }

    [Fact]
    public void ReleaseMethodGeneratedWithHandleStruct()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("HANDLE", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        Assert.True(this.IsMethodGenerated("CloseHandle"));
    }

    [Fact]
    public void NamespaceHandleGetsNoSafeHandle()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("CreatePrivateNamespace", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        Assert.Null(this.FindGeneratedType("ClosePrivateNamespaceSafeHandle"));
    }

    [Fact]
    public void CreateFileUsesSafeHandles()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("CreateFile", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        MethodDeclarationSyntax? createFileMethod = this.FindGeneratedMethod("CreateFile");
        Assert.NotNull(createFileMethod);
        Assert.Equal("CloseHandleSafeHandle", Assert.IsType<IdentifierNameSyntax>(createFileMethod!.ReturnType).Identifier.ValueText);
        Assert.Equal("CloseHandleSafeHandle", Assert.IsType<IdentifierNameSyntax>(createFileMethod.ParameterList.Parameters.Last().Type).Identifier.ValueText);
    }

    [Fact]
    public void BSTR_FieldsDoNotBecomeSafeHandles()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("DebugPropertyInfo", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        StructDeclarationSyntax structDecl = Assert.IsType<StructDeclarationSyntax>(this.FindGeneratedType("DebugPropertyInfo"));
        var bstrField = structDecl.Members.OfType<FieldDeclarationSyntax>().First(m => m.Declaration.Variables.Any(v => v.Identifier.ValueText == "m_bstrName"));
        Assert.Equal("BSTR", ((IdentifierNameSyntax)bstrField.Declaration.Type).Identifier.ValueText);
    }

    [Fact]
    public void GetLastErrorGenerationThrowsWhenExplicitlyCalled()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.Throws<NotSupportedException>(() => this.generator.TryGenerate("GetLastError", CancellationToken.None));
    }

    [Fact(Skip = "https://github.com/microsoft/win32metadata/issues/129")]
    public void DeleteObject_TakesTypeDefStruct()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("DeleteObject", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        MethodDeclarationSyntax? deleteObjectMethod = this.FindGeneratedMethod("DeleteObject");
        Assert.NotNull(deleteObjectMethod);
        Assert.Equal("HGDIOBJ", Assert.IsType<IdentifierNameSyntax>(deleteObjectMethod!.ParameterList.Parameters[0].Type).Identifier.ValueText);
    }

    [Fact]
    public void CollidingStructNotGenerated()
    {
        const string test = @"
namespace Microsoft.Windows.Sdk
{
    internal enum FILE_CREATE_FLAGS
    {
        CREATE_NEW = 1,
        CREATE_ALWAYS = 2,
        OPEN_EXISTING = 3,
        OPEN_ALWAYS = 4,
        TRUNCATE_EXISTING = 5,
    }
}
";
        this.compilation = this.compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(test, path: "test.cs"));
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("CreateFile", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    [Fact]
    public void FullGeneration()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        this.generator.GenerateAll(CancellationToken.None);
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics(logGeneratedCode: false);
    }

    private static ImmutableArray<Diagnostic> FilterDiagnostics(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Where(d => d.Severity > DiagnosticSeverity.Hidden).ToImmutableArray();

    private void CollectGeneratedCode(Generator generator)
    {
        var compilationUnits = generator.GetCompilationUnits(CancellationToken.None);
        var syntaxTrees = new List<SyntaxTree>(compilationUnits.Count);
        foreach (var unit in compilationUnits)
        {
            // Our syntax trees aren't quite right. And anyway the source generator API only takes text anyway so it doesn't _really_ matter.
            // So render the trees as text and have C# re-parse them so we get the same compiler warnings/errors that the user would get.
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(unit.Value.ToFullString(), this.parseOptions, path: unit.Key));
        }

        this.compilation = this.compilation.AddSyntaxTrees(syntaxTrees);
    }

    private MethodDeclarationSyntax? FindGeneratedMethod(string name) => this.compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()).FirstOrDefault(md => md.Identifier.ValueText == name);

    private BaseTypeDeclarationSyntax? FindGeneratedType(string name) => this.compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()).FirstOrDefault(md => md.Identifier.ValueText == name);

    private bool IsMethodGenerated(string name) => this.FindGeneratedMethod(name) is object;

    private void AssertNoDiagnostics(bool logGeneratedCode = true)
    {
        var diagnostics = FilterDiagnostics(this.compilation.GetDiagnostics());
        this.LogDiagnostics(diagnostics);

        var emitDiagnostics = ImmutableArray<Diagnostic>.Empty;
        bool? emitSuccessful = null;
        if (diagnostics.IsEmpty)
        {
            var emitResult = this.compilation.Emit(peStream: Stream.Null, xmlDocumentationStream: Stream.Null);
            emitSuccessful = emitResult.Success;
            emitDiagnostics = FilterDiagnostics(emitResult.Diagnostics);
            this.LogDiagnostics(emitDiagnostics);
        }

        if (logGeneratedCode)
        {
            this.LogGeneratedCode();
        }

        Assert.Empty(diagnostics);
        if (emitSuccessful.HasValue)
        {
            Assert.Empty(emitDiagnostics);
            Assert.True(emitSuccessful);
        }
    }

    private void LogDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            this.logger.WriteLine(diagnostic.ToString());
        }
    }

    private void LogGeneratedCode()
    {
        foreach (SyntaxTree tree in this.compilation.SyntaxTrees)
        {
            this.logger.WriteLine(FileSeparator);
            this.logger.WriteLine($"{tree.FilePath} content:");
            this.logger.WriteLine(FileSeparator);
            using var lineWriter = new NumberedLineWriter(this.logger);
            tree.GetRoot().WriteTo(lineWriter);
            lineWriter.WriteLine(string.Empty);
        }
    }
}
