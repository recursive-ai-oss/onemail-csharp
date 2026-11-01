using Android.App;
using Android.OS;
using Android.Util;
using System.Threading;

// The SMTP check talks to a server on the loopback interface.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]

namespace RecursiveAI.OneMail.AndroidSmoke;

/// <summary>Runs the shared smoke checks once on a worker thread and logs one line, "mobile smoke: ok" or "mobile smoke: FAILED",
/// under the tag OneMailSmoke, then closes. There is no UI.</summary>
[Activity(Label = "OneMail smoke", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const string Tag = "OneMailSmoke";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var thread = new Thread(() =>
        {
            int exit;
            try
            {
                exit = RecursiveAI.OneMail.AotSmoke.Program.Run();
            }
            catch (Exception e)
            {
                Log.Error(Tag, "unhandled: " + e);
                exit = 1;
            }

            // Console output goes to logcat under the tag DOTNET; this line is the one a script reads.
            Log.Info(Tag, exit == 0 ? "mobile smoke: ok" : "mobile smoke: FAILED");
            RunOnUiThread(Finish);
        }, 16 * 1024 * 1024);
        thread.Start();
    }
}
