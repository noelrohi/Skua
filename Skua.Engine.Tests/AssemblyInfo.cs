using System.Runtime.Versioning;

// The Engine and its Control Surfaces run on macOS; these tests start them as processes over Unix sockets.
[assembly: UnsupportedOSPlatform("windows")]
