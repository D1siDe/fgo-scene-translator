using System.Text;
using Android.Runtime;

namespace FGOPatcher.Maui;

/// <summary>
/// Shell от имени adb (uid shell) через Shizuku. Без Java-биндингов: классы Shizuku лежат в APK как jar (libs\shizuku-*.jar),
/// вызываются через JNI по имени, поэтому нет ошибок «не найден тип» при сборке.
/// </summary>
public static class ShizukuShell
{
    const string ClassName = "rikka/shizuku/Shizuku";

    public sealed record ShellResult(int Exit, byte[] Out, string Err)
    {
        public string Text => Encoding.UTF8.GetString(Out);
    }

    public static bool ClassPresent()
    {
        try
        {
            var c = JNIEnv.FindClass(ClassName);
            return c != IntPtr.Zero;                 // FindClass отдаёт глобальную ссылку, её не удаляем
        }
        catch { return false; }
    }

    static T Static<T>(string name, string sig, Func<IntPtr, IntPtr, T> call)
    {
        var cls = JNIEnv.FindClass(ClassName);
        var mid = JNIEnv.GetStaticMethodID(cls, name, sig);
        return call(cls, mid);
    }

    public static bool PingBinder() => Static("pingBinder", "()Z", (c, m) => JNIEnv.CallStaticBooleanMethod(c, m));

    /// <summary>0 = разрешено (PackageManager.PERMISSION_GRANTED).</summary>
    public static int CheckPermission() => Static("checkSelfPermission", "()I", (c, m) => JNIEnv.CallStaticIntMethod(c, m));

    public static void RequestPermission(int code) =>
        Static<object?>("requestPermission", "(I)V", (c, m) => { JNIEnv.CallStaticVoidMethod(c, m, new JValue(code)); return null; });

    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Выполняет команду в sh. stdin (если задан) подаётся на вход процесса, например для "cat > файл".</summary>
    public static ShellResult Run(string command, Stream? stdin = null)
    {
        IntPtr procHandle;
        var cls = JNIEnv.FindClass(ClassName);                  // глобальная ссылка (кэшируется), не удаляем
        var mid = JNIEnv.GetStaticMethodID(cls, "newProcess",
            "([Ljava/lang/String;[Ljava/lang/String;Ljava/lang/String;)Lrikka/shizuku/ShizukuRemoteProcess;");
        var arr = JNIEnv.NewArray(new[] { "sh", "-c", command });       // локальная ссылка: освободится сама, вручную не удаляем
        procHandle = JNIEnv.CallStaticObjectMethod(cls, mid, new JValue(arr), new JValue(IntPtr.Zero), new JValue(IntPtr.Zero));

        using var proc = Java.Lang.Object.GetObject<Java.Lang.Process>(procHandle, JniHandleOwnership.TransferLocalRef)
                         ?? throw new InvalidOperationException("Shizuku не вернул процесс");
        var outTask = Task.Run(() => ReadAll(proc.InputStream!));
        var errTask = Task.Run(() => ReadAll(proc.ErrorStream!));
        var os = proc.OutputStream;
        if (os != null)
        {
            if (stdin != null)
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = stdin.Read(buf, 0, buf.Length)) > 0) os.Write(buf, 0, n);
                os.Flush();
            }
            os.Dispose();
        }
        int exit = proc.WaitFor();
        var output = outTask.GetAwaiter().GetResult();
        var error = errTask.GetAwaiter().GetResult();
        return new ShellResult(exit, output, Encoding.UTF8.GetString(error).Trim());
    }

    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1 << 16];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }
}
