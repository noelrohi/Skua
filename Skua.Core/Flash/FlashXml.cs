using Skua.Core.Utils;
using System.Dynamic;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Skua.Core.Flash;

/// <summary>
/// The Flash external API's XML codec: the invoke requests and return values passed between Skua and the Game Client.
/// Every <see cref="Interfaces.IFlashUtil"/> shares it, whatever carries the XML.
/// </summary>
public static class FlashXml
{
    /// <summary>Builds the invoke request for a call to <paramref name="function"/> in the Game Client.</summary>
    public static string Invoke(string function, params object[] args)
    {
        StringBuilder req = new StringBuilder().Append($"<invoke name=\"{function}\" returntype=\"xml\">");
        if (args.Length > 0)
        {
            req.Append("<arguments>");
            args.ForEach(o => req.Append(ToFlashXml(o)));
            req.Append("</arguments>");
        }
        req.Append("</invoke>");
        return req.ToString();
    }

    /// <summary>Reads an invoke request from the Game Client into its function name and arguments.</summary>
    public static (string Function, object[] Args) ReadInvoke(string request)
    {
        XElement el = XElement.Parse(request);
        string function = el.Attribute("name")!.Value;
        object[] args = el.Elements().Select(x => FromFlashXml(x)).ToArray();
        return (function, args);
    }

    /// <summary>Reads the return value of a call as <paramref name="type"/>, or null when the call returned nothing.</summary>
    public static object? ReadReturn(string result, Type type)
    {
        XElement el = XElement.Parse(result);
        return el is null || el.FirstNode is null ? default : Convert.ChangeType(el.FirstNode.ToString(), type);
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

    public static object FromFlashXml(XElement el)
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
                IDictionary<string, object?> d = new ExpandoObject();
                el.Elements().ForEach(e => d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First()));
                return d;

            default:
                return el.Value;
        }
    }
}
