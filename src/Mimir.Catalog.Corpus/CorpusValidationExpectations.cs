namespace Mimir.Catalog.Corpus;

/// <summary>Accepted expected reference constants for the published Pass-B corpus.</summary>
public static class CorpusValidationExpectations
{
    public const long ConceptRows = 7_403_488;
    public const long LexicalRows = 7_121_880;
    public const long InstanceOfRows = 3_202_468;
    public const long SubclassOfRows = 5_233_394;

    public const long T1 = 3_036_124;
    public const long T2 = 4_480_182;
    public const long T1IntersectT2 = 112_818;
    public const long T2Only = 4_367_364;
    public const long ObservedConcepts = 7_403_468;
    public const long UnobservedTail = 20;

    public const long T1EnLabels = 2_321_048;
    public const long T1NbLabels = 108_605;
    public const long T1EnAliases = 370_246;
    public const long T1NbAliases = 14_478;
    public const long T2OnlyEnLabels = 4_217_221;
    public const long T2OnlyNbLabels = 90_282;

    public const long P279DistinctSubjects = 4_467_638;
    public const long P279DistinctObjects = 297_089;

    // Pass-A full-source references (diagnostics).
    public const long FullItems = 121_439_429;
    public const long FullP31 = 128_083_539;
    public const long FullLabelEn = 92_798_796;
    public const long FullLabelNb = 4_347_263;
    public const long FullAliasEn = 14_810_379;
    public const long FullAliasNb = 578_824;
    public const double SampleFraction = 0.025;
}
