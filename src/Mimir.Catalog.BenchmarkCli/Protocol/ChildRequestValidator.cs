using System.Text;
using System.Text.Json;

namespace Mimir.Catalog.BenchmarkCli.Protocol;

/// <summary>Strict request-file reading and authoritative identity validation.</summary>
public static class ChildRequestValidator
{
    /// <summary>Reads exactly one JSON object document and validates shape + authoritative identities.</summary>
    public static ChildRequestEnvelope ReadAndValidate(string requestPath)
    {
        if (!File.Exists(requestPath)) throw new JsonException($"request file not found: {requestPath}");
        byte[] bytes = File.ReadAllBytes(requestPath);
        var request = ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(bytes);
        var errors = Validate(request);
        if (errors.Count > 0)
            throw new RequestValidationException(string.Join("; ", errors));
        return request;
    }

    /// <summary>Field/identity validation; returns human-readable reasons (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(ChildRequestEnvelope r)
    {
        var errors = new List<string>();
        if (r.ProtocolVersion != ProtocolConstants.ChildProtocolVersion)
            errors.Add($"unsupported protocol version '{r.ProtocolVersion}'");
        Require(r.CandidateId, nameof(r.CandidateId), errors);
        Require(r.CandidateConfigId, nameof(r.CandidateConfigId), errors);
        Require(r.WorkloadId, nameof(r.WorkloadId), errors);
        Require(r.CorpusId, nameof(r.CorpusId), errors);
        Require(r.CandidatePath, nameof(r.CandidatePath), errors);
        Require(r.WorkloadPath, nameof(r.WorkloadPath), errors);
        Require(r.RunId, nameof(r.RunId), errors);
        if (r.Operation is null or "") errors.Add("operation must not be empty");
        if (r.Repetition <= 0) errors.Add("repetition must be positive");
        if (r.CandidateId != CandidateAIdentity.CandidateId)
            errors.Add("candidate id mismatch");
        if (r.CandidateConfigId != CandidateAIdentity.CandidateConfigId)
            errors.Add("candidate config id mismatch");
        if (r.WorkloadId != CandidateAIdentity.WorkloadId)
            errors.Add("workload id mismatch");
        if (r.CorpusId != CandidateAIdentity.CorpusId)
            errors.Add("corpus id mismatch");
        return errors;
    }

    private static void Require(string value, string field, List<string> errors)
    {
        if (value is null or "") errors.Add($"{field} must be non-empty");
    }
}

public sealed class RequestValidationException : Exception
{
    public RequestValidationException(string message) : base(message) { }
}
