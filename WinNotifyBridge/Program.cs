using System;
using System.ServiceProcess;

namespace WinNotifyBridge
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            // Support a console/debug mode by passing the single argument "console".
            // e.g. run the listener/service from Visual Studio as a Console app for debugging.
            if (args != null && args.Length > 0 && string.Equals(args[0], "console", StringComparison.OrdinalIgnoreCase))
            {
                var svc = new Service1();
                svc.RunAsConsole();
                return;
            }

            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new Service1()
            };
            ServiceBase.Run(ServicesToRun);
        }
    }
}
