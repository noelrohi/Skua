using System.Reflection;
using System.Runtime.Loader;

namespace Skua.Core;

public class ScriptLoadContext : AssemblyLoadContext
{
    public ScriptLoadContext() : base(isCollectible: true)
    {
    }

    /// <remarks>
    /// Every include a Script uses is loaded into its context while it compiles, under the cache's lock, so nothing is looked up in the
    /// cache here: other versions of an include sit there too, and the cleanup may delete any of them once the compile ends. Anything else
    /// comes from the default context.
    /// </remarks>
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
