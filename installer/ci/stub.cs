// SPDX-License-Identifier: MIT
// CI stand-in for every SplitPlay executable in a fake stage folder (installer smoke test only).
// Built with the .NET Framework csc.exe that ships with Windows (C# 5, no SDK needed).
//
//   <any name>.exe service install     registers the "SplitPlay" service pointing at SplitPlay.Service.exe
//   <any name>.exe service uninstall   stops and deletes it
//   --stub-service (set by install)    runs as an idle Windows service until the SCM stops it
//   anything else                      prints its name and arguments and exits 0
// Every call is appended to %ProgramData%\SplitPlay-ci-stub-calls.log so that the smoke test can check which
// commands the installer ran (e.g. "splitplay.exe host prepare --skip-termwrap").
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

class StubService : ServiceBase
{
    public StubService()
    {
        ServiceName = "SplitPlay";
        CanStop = true;
    }
}

static class Program
{
    const string Name = "SplitPlay";

    static int Run(string file, string args)
    {
        Console.WriteLine("> " + file + " " + args);
        ProcessStartInfo psi = new ProcessStartInfo(file, args);
        psi.UseShellExecute = false;
        using (Process p = Process.Start(psi))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    static int Install()
    {
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SplitPlay.Service.exe");
        // binPath= "\"C:\...\SplitPlay.Service.exe\" --stub-service"
        return Run("sc.exe", "create " + Name + " binPath= \"\\\"" + exe + "\\\" --stub-service\" start= delayed-auto DisplayName= \"SplitPlay Host Service\"");
    }

    static int Uninstall()
    {
        try
        {
            using (ServiceController sc = new ServiceController(Name))
            {
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("stop: " + e.Message);
        }
        return Run("sc.exe", "delete " + Name);
    }

    static void Record(string[] args)
    {
        try
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SplitPlay-ci-stub-calls.log");
            File.AppendAllText(log, Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName) + " " + string.Join(" ", args) + Environment.NewLine);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("could not record the call: " + e.Message);
        }
    }

    static int Main(string[] args)
    {
        Record(args);
        if (args.Length >= 2 && args[0] == "service")
        {
            if (args[1] == "install") return Install();
            if (args[1] == "uninstall") return Uninstall();
            Console.Error.WriteLine("unknown service command: " + args[1]);
            return 2;
        }
        if (args.Length == 1 && args[0] == "--stub-service")
        {
            ServiceBase.Run(new StubService());
            return 0;
        }
        Console.WriteLine("SplitPlay CI stub: " + Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName) + " " + string.Join(" ", args));
        return 0;
    }
}
