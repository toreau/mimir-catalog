namespace Mimir.Catalog.Corpus;

/// <summary>
/// Pure, testable gate logic for validator high-risk rules. Row-group boundary
/// independence comes from persistent state that survives across row groups.
/// </summary>
public static class ValidationGates
{
    /// <summary>
    /// Per-subject strictly-ascending target order, independent of row-group
    /// boundaries. State persists across row-group callbacks.
    /// </summary>
    public sealed class TargetOrderState
    {
        private long _currentSubject = long.MinValue;
        private bool _hasSubject;
        private bool _hasPrevious;
        private long _previousTarget;

        /// <summary>True when this Step started a new subject group.</summary>
        public bool NewGroup { get; private set; }

        public string? Step(long subject, long target)
        {
            bool same = _hasSubject && _currentSubject == subject;
            NewGroup = !same;
            if (!same)
            {
                _currentSubject = subject;
                _hasSubject = true;
                _hasPrevious = false;
                _previousTarget = 0;
            }
            if (_hasPrevious && target <= _previousTarget)
                return "target not strictly ascending within subject";
            _previousTarget = target;
            _hasPrevious = true;
            return null;
        }
    }

    /// <summary>
    /// Frozen within-QID lexical ordering (en before nb, label before alias,
    /// ordinal value). Explicit previous-state flag so empty raw values are not
    /// treated as "no previous key".
    /// </summary>
    public sealed class LexicalOrderState
    {
        private int _prevLang = -1;
        private int _prevKind = -1;
        private string _prevValue = string.Empty;
        private bool _hasPrevious;

        public void Reset() => _hasPrevious = false;

        public string? Step(string lang, string kind, string value)
        {
            int lr = lang == "en" ? 0 : 1;
            int kr = kind == "label" ? 0 : 1;
            if (_hasPrevious)
            {
                int c = _prevLang.CompareTo(lr);
                if (c == 0) c = _prevKind.CompareTo(kr);
                if (c == 0) c = string.CompareOrdinal(_prevValue, value);
                if (c > 0) return "lexical rows not in frozen ordering within QID";
            }
            _prevLang = lr;
            _prevKind = kr;
            _prevValue = value;
            _hasPrevious = true;
            return null;
        }
    }

    public sealed record ConceptChecks(long TailHashQualified, IReadOnlyList<string> TailHashQualifiedQids);

    /// <summary>
    /// Flags/tail/hash gate checks over a full Concept array (ordinal = array
    /// index). Returns gate errors. Tier counts are validated separately against
    /// expectations. tailCount = number of trailing rows that are the declared
    /// unobserved-T2 tail.
    /// </summary>
    public static ConceptChecks CheckConcept(long[] qids, bool[] in1, bool[] in2, int tailCount, List<string> errors)
    {
        for (int i = 0; i < qids.Length; i++)
        {
            if (!in1[i] && !in2[i]) errors.Add($"Concept (false,false) flags at ordinal {i}");
            if (in1[i] && !CorpusHash.IsT1(qids[i])) errors.Add($"Concept InT1=true hash non-member Q{qids[i]} at ordinal {i}");
        }

        int tailStart = qids.Length - tailCount;
        if (tailStart < 0)
        {
            errors.Add("tail start negative");
            return new ConceptChecks(0, Array.Empty<string>());
        }
        long qualified = 0;
        var qualifiedQids = new List<string>();
        for (int i = tailStart; i < qids.Length; i++)
        {
            if (in1[i]) errors.Add($"tail row ordinal {i} has InT1=true");
            if (!in2[i]) errors.Add($"tail row ordinal {i} has InT2=false");
            if (i > tailStart && qids[i] <= qids[i - 1]) errors.Add("tail QIDs not strictly ascending");
            if (CorpusHash.IsT1(qids[i])) { qualified++; qualifiedQids.Add($"Q{qids[i]}"); }
        }
        for (int i = 0; i < tailStart; i++)
            if (!in1[i] && CorpusHash.IsT1(qids[i]))
                errors.Add($"hash-qualified non-T1 concept Q{qids[i]} at ordinal {i} outside declared tail");
        return new ConceptChecks(qualified, qualifiedQids);
    }
}
