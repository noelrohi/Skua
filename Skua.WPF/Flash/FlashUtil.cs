using AxShockwaveFlashObjects;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Skua.WPF.Flash;

public class FlashUtil : IFlashUtil
{
    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private AxShockwaveFlash? Flash;

    public event FlashCallHandler? FlashCall;

    public void InitializeFlash()
    {
        try
        {
            if (!EoLHook.IsHooked)
                EoLHook.Hook();

            CleanupOldFlash();

            AxShockwaveFlash flash = new();
            flash.BeginInit();
            flash.Name = "flash";
            flash.Dock = DockStyle.Fill;
            flash.TabIndex = 0;
            flash.FlashCall += CallHandler;

            _messenger.Send<FlashChangedMessage<AxShockwaveFlash>>(new(flash));
            flash.EndInit();
            Flash = flash;
            byte[] swf = File.ReadAllBytes("skua.swf");
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(8 + swf.Length);
            writer.Write(1432769894);
            writer.Write(swf.Length);
            writer.Write(swf);
            writer.Seek(0, SeekOrigin.Begin);
            flash.OcxState = new AxHost.State(stream, 1, false, null);
        }
        catch
        {
            if (MessageBox.Show($"Please, uninstall and then reinstall CleanFlash via the Gif provided \r\nDo you want view the instructional Gif?", "Clean Flash missing/installed incorrectly", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes)
                Ioc.Default.GetRequiredService<IProcessService>().OpenLink("https://imgur.com/ztsLYZ1");
            Environment.Exit(0);
        }
        finally
        {
            EoLHook.Unhook();
        }
    }

    private void CleanupOldFlash()
    {
        if (Flash == null) return;

        Flash.FlashCall -= CallHandler;

        try
        {
            Flash.Stop();
        }
        catch { /* ignored */ }

        object ocx = null;
        try
        {
            ocx = Flash.GetOcx();
        }
        catch { /* ignored */ }

        Flash.Dispose();
        Flash = null;

        if (ocx != null)
        {
            try
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ocx);
            }
            catch { /* ignored */ }
        }
    }


    private void CallHandler(object sender, _IShockwaveFlashEvents_FlashCallEvent e)
    {
        (string function, object[] args) = FlashXml.ReadInvoke(e.request);
        FlashCall?.Invoke(function, args);
    }

    public string Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

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
            string result = Flash?.CallFunction(FlashXml.Invoke(function, args))!;
            return FlashXml.ReadReturn(result, type);
        }
        catch (Exception e)
        {
            _messenger.Send<FlashErrorMessage>(new(e, function, args));
            return default;
        }
    }

    public static string ToFlashXml(object o) => FlashXml.ToFlashXml(o);

    public object FromFlashXml(XElement el) => FlashXml.FromFlashXml(el);

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
        try
        {
            CleanupOldFlash();
        }
        catch { /* ignored */ }
        finally
        {
            EoLHook.Unhook();
        }
    }
}