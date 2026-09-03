using System.Reflection;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class AdapterSurfaceTests
{
    private static readonly Type[] SurfaceTypes =
    [
        typeof(IStorageCandidate),
        typeof(IAnalyticalCandidate),
        typeof(ConceptHit),
        typeof(ConceptRow),
        typeof(LexicalHit),
        typeof(LexicalRow),
        typeof(EdgeRow),
        typeof(A5Row),
        typeof(AnalyticalOperation),
    ];

    [Fact]
    public void AdapterSurface_NoStorageEnginePublicTypes()
    {
        foreach (var type in SurfaceTypes)
        {
            Assert.StartsWith("Mimir.Catalog.Benchmark", type.FullName!, StringComparison.Ordinal);
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var referenced in ReferencedTypes(member))
                {
                    Assert.DoesNotContain("Sqlite", referenced.FullName ?? referenced.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("System.Data", referenced.FullName ?? referenced.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("DuckDB", referenced.FullName ?? referenced.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private static IEnumerable<Type> ReferencedTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo m:
                yield return m.ReturnType;
                foreach (var p in m.GetParameters()) yield return p.ParameterType;
                break;
            case PropertyInfo p:
                yield return p.PropertyType;
                break;
            case FieldInfo f:
                yield return f.FieldType;
                break;
            case ConstructorInfo c:
                foreach (var p in c.GetParameters()) yield return p.ParameterType;
                break;
        }
    }

    [Fact]
    public void AnalyticalConceptScan_ExposesFullLogicalRow()
    {
        var method = typeof(IAnalyticalCandidate).GetMethod("ScanConcept")!;
        Assert.Equal(typeof(IEnumerable<ConceptRow>), method.ReturnType);
        Assert.Equal(typeof(long), typeof(ConceptRow).GetProperty("Qid")!.PropertyType);
        Assert.Equal(typeof(bool), typeof(ConceptRow).GetProperty("InT1")!.PropertyType);
        Assert.Equal(typeof(bool), typeof(ConceptRow).GetProperty("InT2")!.PropertyType);
    }

    [Fact]
    public void ServingResults_AreFullyMaterializedLists()
    {
        Assert.True(typeof(IReadOnlyList<LexicalHit>).IsAssignableFrom(typeof(IStorageCandidate).GetMethod("LookupLexical")!.ReturnType));
        Assert.True(typeof(IReadOnlyList<long>).IsAssignableFrom(typeof(IStorageCandidate).GetMethod("GetInstanceOf")!.ReturnType));
        Assert.True(typeof(IReadOnlyList<LexicalRow>).IsAssignableFrom(typeof(IStorageCandidate).GetMethod("GetLexicalByQid")!.ReturnType));
    }
}
