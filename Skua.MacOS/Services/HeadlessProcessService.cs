using System.Diagnostics;
using Skua.Core.Interfaces;

namespace Skua.MacOS.Services;

/// <summary>Nothing is opened on a headless Mac; each request is only logged.</summary>
public sealed class HeadlessProcessService : IProcessService
{
    public void OpenLink(string link) => Trace.WriteLine($"Not opening link headless: {link}");

    public void OpenVSC() => Trace.WriteLine("Not opening VS Code headless.");

    public void OpenVSC(string path) => Trace.WriteLine($"Not opening VS Code headless: {path}");
}
