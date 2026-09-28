using System.Runtime.Versioning;

// The Mac App's layer (ADR 0006): it talks to its Engine through Skua.Control's types, which are macOS-only.
[assembly: UnsupportedOSPlatform("windows")]
