// PROTOTYPE (#6): IFlashUtil over the Rust Game Host. Call/ToFlashXml/FromFlashXml are copied
// from Skua.WPF/Flash/FlashUtil.cs; only the transport (CallFunction/FlashCall) is swapped.
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System.Dynamic;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace BridgeConsole;

public class MacFlashUtil : IFlashUtil
{
    public static string GameHostExe = "";
    public static string SwfPath = "";
    public static bool ShowGame;

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    public GameHost? Host { get; private set; }

    public event FlashCallHandler? FlashCall;

    public MacFlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public void InitializeFlash()
    {
        Host?.Dispose();
        Host = new GameHost(GameHostExe, SwfPath, ShowGame, Environment.GetEnvironmentVariable("SKUA_GAMEHOST_ARGS")); // #13: e.g. --render-interval-ms=250
        Host.EventXml += CallHandler;
    }

    private void CallHandler(string request)
    {
        XElement el = XElement.Parse(request);
        string function = el.Attribute("name")!.Value;
        object[] args = el.Elements().Select(x => FromFlashXml(x)).ToArray();
        FlashCall?.Invoke(function, args);
    }

    public string Call(string function, params object[] args) => Call<string>(function, args);

    public T Call<T>(string function, params object[] args)
    {
        try
        {
            object o = Call(function, typeof(T), args);
            return o is not null ? (T)o : (T)DefaultProvider.GetDefault<T>(typeof(T));
        }
        catch
        {
            return (T)DefaultProvider.GetDefault<T>(typeof(T));
        }
    }

    public object Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();
        try
        {
            StringBuilder req = new StringBuilder().Append($"<invoke name=\"{function}\" returntype=\"xml\">");
            if (args.Length > 0)
            {
                req.Append("<arguments>");
                args.ForEach(o => req.Append(ToFlashXml(o)));
                req.Append("</arguments>");
            }
            req.Append("</invoke>");
            string result = Host!.Call(req.ToString());
            XElement el = XElement.Parse(result);
            return el is null || el.FirstNode is null ? default : Convert.ChangeType(el.FirstNode.ToString(), type);
        }
        catch (Exception e)
        {
            _messenger.Send<FlashErrorMessage>(new(e, function, args));
            return default;
        }
    }

    public static string ToFlashXml(object o)
    {
        switch (o)
        {
            case null:
                return "<null/>";
            case bool _:
                return $"<{o.ToString()!.ToLower()}/>";
            case double _:
            case float _:
            case long _:
            case int _:
                return $"<number>{o}</number>";
            case ExpandoObject _:
                StringBuilder sb = new StringBuilder().Append("<object>");
                foreach (KeyValuePair<string, object> kvp in (o as IDictionary<string, object>)!)
                    sb.Append($"<property id=\"{kvp.Key}\">{ToFlashXml(kvp.Value)}</property>");
                return sb.Append("</object>").ToString();
            default:
                if (o is Array)
                {
                    StringBuilder _sb = new StringBuilder().Append("<array>");
                    int k = 0;
                    foreach (object el in (o as Array)!)
                        _sb.Append($"<property id=\"{k++}\">{ToFlashXml(el)}</property>");
                    return _sb.Append("</array>").ToString();
                }
                return $"<string>{SecurityElement.Escape(o.ToString())}</string>";
        }
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;
            case "true":
                return true;
            case "false":
                return false;
            case "null":
                return null!;
            case "array":
                return el.Elements().Select(e => FromFlashXml(e)).ToArray();
            case "object":
                dynamic d = new ExpandoObject();
                el.Elements().ForEach(e => d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First()));
                return d;
            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path) => new FlashObject<T>(Call<int>("lnkCreate", path), this);

    public void Dispose() => Host?.Dispose();
}
