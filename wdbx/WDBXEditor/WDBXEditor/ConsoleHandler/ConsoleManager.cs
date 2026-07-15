using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WDBXEditor.Storage;

namespace WDBXEditor.ConsoleHandler
{
    public static class ConsoleManager
    {
        public static bool ConsoleMode { get; set; } = false;

        public static Dictionary<string, HandleCommand> CommandHandlers = new Dictionary<string, HandleCommand>();
        public delegate void HandleCommand(string[] args);

        public static void ConsoleMain(string[] args)
        {
            Database.LoadDefinitions().Wait();

            if (CommandHandlers.ContainsKey(args[0].ToLower()))
            {
                if (!InvokeHandler(args[0], args.Skip(1).ToArray()))
                    Environment.ExitCode = 1;
            }
            else
            {
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                Environment.ExitCode = 1;
            }
        }

        public static bool InvokeHandler(string command, params string[] args)
        {
            try
            {
                command = command.ToLower();
                if (CommandHandlers.ContainsKey(command))
                {
                    CommandHandlers[command].Invoke(args);
                    return true;
                }
                else
                    return false;
            }
            catch (Exception ex)
            {
                // Signal failure to the caller via a non-zero process exit code (the platform's DBC
                // pipeline relies on this to detect imports that didn't fully apply). Errors go to
                // stderr so they're distinguishable from normal output.
                Environment.ExitCode = 1;
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("");
                return false;
            }
        }

        public static void LoadCommandDefinitions()
        {
            //Argument commands
            //DefineCommand("-console", ConsoleManager.LoadConsoleMode);
            DefineCommand("-export", ConsoleCommands.ExportArgCommand);
            DefineCommand("-sqldump", ConsoleCommands.SqlDumpArgCommand);
            DefineCommand("-extract", ConsoleCommands.ExtractCommand);
            DefineCommand("-import", ConsoleCommands.ImportArgCommand);
        }

        /// <summary>
        /// Converts args into keyvalue pair
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static Dictionary<string, string> ParseCommand(string[] args)
        {
            Dictionary<string, string> keyvalues = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (i == args.Length - 1)
                    break;

                string key = args[i].ToLower();
                string value = args[++i];
                if (value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2);

                keyvalues.Add(key, value);
            }

            return keyvalues;
        }

        private static void DefineCommand(string command, HandleCommand handler)
        {
            CommandHandlers[command.ToLower()] = handler;
        }
    }
}
