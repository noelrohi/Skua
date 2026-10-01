using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Skua.Core.Interfaces;

namespace Skua.Engine.Tests;

/// <summary>Builds a test's plugin assembly from source, against the platform and Skua's plugin interfaces.</summary>
public static class PluginCompiler
{
    public static byte[] Compile(string assembly, string source, params MetadataReference[] references)
    {
        IEnumerable<MetadataReference> platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source)],
            [.. platform, MetadataReference.CreateFromFile(typeof(ISkuaPlugin).Assembly.Location), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(image);
        if (!result.Success)
            throw new InvalidOperationException($"{assembly} didn't compile: {string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))}");
        return image.ToArray();
    }
}
