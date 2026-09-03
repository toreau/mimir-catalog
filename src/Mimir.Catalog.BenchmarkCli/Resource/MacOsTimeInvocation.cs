namespace Mimir.Catalog.BenchmarkCli.Resource;

/// <summary>Pure macOS /usr/bin/time wrapper invocation construction.</summary>
public static class MacOsTimeInvocation
{
    public const string TimeExecutable = "/usr/bin/time";

    /// <summary>
    /// Maps an inner child invocation to exactly:
    /// /usr/bin/time -l -o &lt;resource-output-path&gt; &lt;inner...&gt;
    /// Every inner executable/argument is preserved literally (ArgumentList,
    /// no shell, no -a append).
    /// </summary>
    public static Process.ProcessInvocation Wrap(Process.ProcessInvocation inner, string resourceOutputPath)
    {
        var args = new List<string> { "-l", "-o", resourceOutputPath, inner.Executable };
        args.AddRange(inner.Arguments);
        return new Process.ProcessInvocation { Executable = TimeExecutable, Arguments = args };
    }
}
