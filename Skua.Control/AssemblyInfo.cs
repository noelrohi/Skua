using System.Runtime.Versioning;

// The transport is a Unix domain socket guarded by flock; a Windows port would add a named-pipe transport.
[assembly: UnsupportedOSPlatform("windows")]
