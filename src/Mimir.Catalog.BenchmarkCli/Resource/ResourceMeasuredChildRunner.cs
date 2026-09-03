namespace Mimir.Catalog.BenchmarkCli.Resource;

/// <summary>
/// Resource-measured child runner. Reuses ChildProcessRunner unchanged and adds
/// a separate external-RSS axis from the dedicated /usr/bin/time -o file.
/// </summary>
public static class ResourceMeasuredChildRunner
{
    public static async Task<ResourceMeasuredProcessResult> RunAsync(
        Process.ProcessInvocation innerInvocation,
        string resourceOutputPath,
        TimeSpan timeout,
        Protocol.ChildRequestEnvelope request)
    {
        try
        {
            ClearStaleFile(resourceOutputPath);
        }
        catch (Exception ex)
        {
            return new ResourceMeasuredProcessResult
            {
                ProcessResult = new Process.ChildProcessResult
                {
                    Outcome = Process.ProcessOutcome.ProcessStartError,
                    OutputDrainCompleted = false,
                    ValidationError = $"failed to clear resource output file: {ex.Message}",
                },
                ResourceStatus = ResourceMeasurementStatus.Unavailable,
                ResourceOutputPath = resourceOutputPath,
            };
        }

        var wrapper = MacOsTimeInvocation.Wrap(innerInvocation, resourceOutputPath);
        var processResult = await Process.ChildProcessRunner.RunAsync(wrapper, timeout, request).ConfigureAwait(false);
        return Classify(processResult, resourceOutputPath);
    }

    /// <summary>Classification over an already-produced process result + dedicated resource file.</summary>
    public static ResourceMeasuredProcessResult Classify(Process.ChildProcessResult processResult, string resourceOutputPath)
    {
        if (processResult.Outcome == Process.ProcessOutcome.ProcessStartError)
        {
            return new ResourceMeasuredProcessResult
            {
                ProcessResult = processResult,
                ResourceStatus = ResourceMeasurementStatus.Unavailable,
                ResourceOutputPath = resourceOutputPath,
            };
        }

        if (processResult.TimedOut)
        {
            if (!processResult.WrapperExitObserved)
            {
                // Wrapper may still be writing; never trust a partial/active file.
                return new ResourceMeasuredProcessResult
                {
                    ProcessResult = processResult,
                    ResourceStatus = ResourceMeasurementStatus.Unavailable,
                    ResourceOutputPath = resourceOutputPath,
                };
            }

            // Wrapper exit observed: a complete strict file may be trusted; any
            // malformed/incomplete output stays Unavailable (interrupted time).
            if (TryReadStrict(resourceOutputPath, out long bytes))
            {
                return new ResourceMeasuredProcessResult
                {
                    ProcessResult = processResult,
                    ResourceStatus = ResourceMeasurementStatus.Valid,
                    ExternalPeakRssBytes = bytes,
                    ResourceOutputPath = resourceOutputPath,
                };
            }
            return new ResourceMeasuredProcessResult
            {
                ProcessResult = processResult,
                ResourceStatus = ResourceMeasurementStatus.Unavailable,
                ResourceOutputPath = resourceOutputPath,
            };
        }

        // Completed non-timeout wrapper: resource evidence is required.
        string? error = null;
        if (!File.Exists(resourceOutputPath))
        {
            error = $"resource output file missing: {resourceOutputPath}";
        }
        else if (!MacOsTimeRssParser.TryParse(File.ReadAllText(resourceOutputPath), out long bytes, out string? parseError))
        {
            error = parseError;
        }
        else
        {
            return new ResourceMeasuredProcessResult
            {
                ProcessResult = processResult,
                ResourceStatus = ResourceMeasurementStatus.Valid,
                ExternalPeakRssBytes = bytes,
                ResourceOutputPath = resourceOutputPath,
            };
        }

        return new ResourceMeasuredProcessResult
        {
            ProcessResult = processResult,
            ResourceStatus = ResourceMeasurementStatus.Error,
            ResourceError = error,
            ResourceOutputPath = resourceOutputPath,
        };
    }

    internal static void ClearStaleFile(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new IOException($"resource output parent directory does not exist: {parent}");
        File.Delete(path); // no-op when absent
        if (File.Exists(path))
            throw new IOException($"failed to clear stale resource output file: {path}");
    }

    private static bool TryReadStrict(string path, out long bytes)
    {
        bytes = 0;
        if (!File.Exists(path)) return false;
        return MacOsTimeRssParser.TryParse(File.ReadAllText(path), out bytes, out _);
    }
}
