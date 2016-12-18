using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Name comes from "Banjo V200 Polypropylene Ball Valve"
/// Polypropylene is used to make cany wrappers.
/// This program is a wrapper for other console apps. It provides Telnet access to a console app's IO streams.
/// Like the Banjo V200, this program is a valve to the internet.
/// </summary>

namespace Banjo
{
    class Program
    {
        private const int MAX_AUTH_TRIES = 3;
        private const int BUFFER_SIZE = 1024;
        private static byte[] buffer = new byte[BUFFER_SIZE];
        private static Socket serverSocket = null;
        private static string password = "1234";
        private static string consoleCommand = "cmd.exe";
        private static string consoleArguments = "";
        private static string consoleWorkingDirectory = "C:\\";
        private static Process process = null;
        private static Socket clientSocket = null;
        private static bool isAuthenticated;
        private static int authTries = 0;
        private static StreamWriter logWriter = null;
        private static System.Timers.Timer logFlushTimer = new System.Timers.Timer(15000);
        private static string clientMessage = "";

        static void Main(string[] args)
        {
            SetConsoleCtrlHandler(new EventHandler(ConsoleCtrlHandler), true);

            if (args.Length == 0)
            {
                Console.WriteLine("USAGE: BANJO [-ip 0.0.0.0] [-p 23] [-pw 1234] [-c \"cmd.exe\"] [-a \"parameters\"] [-w \"C:\\\"] [-log \"C:\\log.txt\"]");
                Environment.Exit(0);
            }

            Log("Starting Banjo...");

            IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 23);

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "/ip":
                        case "-ip":
                            endPoint.Address = IPAddress.Parse(args[++i]);
                            break;
                        case "/p":
                        case "-p":
                            endPoint.Port = int.Parse(args[++i]);
                            break;
                        case "/pw":
                        case "-pw":
                            password = args[++i];
                            break;
                        case "/c":
                        case "-c":
                            consoleCommand = args[++i];
                            break;
                        case "/a":
                        case "-a":
                            consoleArguments = args[++i];
                            break;
                        case "/w":
                        case "-w":
                            consoleWorkingDirectory = args[++i];
                            break;
                        case "/log":
                        case "-log":
                            string filename = args[++i];
                            int j = 1;
                            while (File.Exists(filename))
                            {
                                filename = string.Format("{0}({1}){2}", Path.Combine(Path.GetDirectoryName(args[i]), Path.GetFileNameWithoutExtension(args[i])), j++, Path.GetExtension(args[i]));
                            }
                            if (args[i] != filename)
                                File.Move(args[i], filename);
                            logWriter = new StreamWriter(args[i]);
                            break;
                        default:
                            Log("ERROR: Invalid parameter.");
                            Exit(-1);
                            break;
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                Log("ERROR: Missing argument.");
                Exit(-1);
            }
            catch (FormatException)
            {
                Log("ERROR: Invalid IP address.");
                Exit(-1);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }

            try
            {
                serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                serverSocket.Bind(endPoint);
                serverSocket.Listen(0);
                serverSocket.BeginAccept(new AsyncCallback(ListenAsyncCallback), serverSocket);
                Log("Banjo listening.");
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                Exit(-1);
            }

            try
            {
                ProcessStartInfo processStartInfo = new ProcessStartInfo(consoleCommand)
                {
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    WorkingDirectory = consoleWorkingDirectory,
                    Arguments = consoleArguments
                };
                process = new Process()
                {
                    StartInfo = processStartInfo
                };
                process.Start();
                StartConsoleProcessReader(process.StandardOutput);
                StartConsoleProcessReader(process.StandardError);

                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                Console.Title = string.Format("Banjo v{1}.{2} rev {3} - ({0})", consoleCommand, version.Major, version.Minor, version.Revision);

                Log("'{0}' started.", consoleCommand);
                Log("Parameters '{0}'.", consoleArguments);
                Log("Working Directory '{0}'.", consoleWorkingDirectory);

                if (logWriter != null)
                {
                    logFlushTimer.Elapsed += LogFlushTimer_Elapsed;
                    logFlushTimer.AutoReset = true;
                    logFlushTimer.Start();
                }

                while (!process.HasExited)
                    if (Console.KeyAvailable)
                        process.StandardInput.Write(Console.ReadKey().KeyChar);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }

            Log("Process terminated with exit code ({0}).", process.ExitCode);
            Exit(0);
        }

        private static void LogFlushTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                lock (logWriter)
                {
                    logWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private static void ListenAsyncCallback(IAsyncResult ar)
        {
            try
            {
                Socket serverSocket = (Socket)ar.AsyncState;
                Socket _clientSocket = serverSocket.EndAccept(ar);

                IPEndPoint clientEndPoint = _clientSocket.RemoteEndPoint as IPEndPoint;
                Log("Received connection from {0}:{1}.", clientEndPoint.Address, clientEndPoint.Port);

                // flush socket
                _clientSocket.Receive(buffer);

                byte[] prompt = Encoding.ASCII.GetBytes(": ");
                _clientSocket.Send(prompt, 0, prompt.Length, SocketFlags.None);

                // serverSocket.BeginAccept(new AsyncCallback(ListenAsyncCallback), serverSocket);
                _clientSocket.BeginReceive(buffer, 0, BUFFER_SIZE, SocketFlags.None, new AsyncCallback(BeginReceiveAsyncCallback), _clientSocket);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }
        }

        private static void BeginReceiveAsyncCallback(IAsyncResult ar)
        {
            try
            {
                Socket _clientSocket = (Socket)ar.AsyncState;
                int length = _clientSocket.EndReceive(ar);

                if (length > 0)
                {
                    string message = Encoding.ASCII.GetString(buffer, 0, length);
                    if (message == "\r\n")
                    {
                        if (isAuthenticated)
                        {
                            process.StandardInput.WriteLine(clientMessage);
                        }
                        else
                        {
                            if (3 > authTries++)
                                isAuthenticated = clientMessage == password;

                            if (isAuthenticated)
                            {
                                clientSocket = _clientSocket;
                                Log("Client authenticated.");
                            }
                            else
                            {
                                Log("Incorrect password.");
                                byte[] prompt = Encoding.ASCII.GetBytes(": ");
                                _clientSocket.Send(prompt, 0, prompt.Length, SocketFlags.None);
                            }
                        }
                        clientMessage = "";
                    }
                    else
                        clientMessage += message;

                    _clientSocket.BeginReceive(buffer, 0, BUFFER_SIZE, SocketFlags.None, new AsyncCallback(BeginReceiveAsyncCallback), _clientSocket);
                }
                else
                {
                    isAuthenticated = false;
                    clientSocket.Close();
                    clientSocket = null;
                    Log("Client disconnected.");
                    serverSocket.BeginAccept(new AsyncCallback(ListenAsyncCallback), serverSocket);
                }
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }
        }

        private static void StartConsoleProcessReader(StreamReader reader)
        {
            try
            {
                new Thread(() =>
                {
                    int data;

                    while (true)
                    {
                        while ((data = reader.Read()) >= 0)
                        {
                            if (clientSocket != null)
                                clientSocket.Send(BitConverter.GetBytes(data), 0, 1, SocketFlags.None);
                            Console.Write((char)data);
                        }
                    }
                }).Start();
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }
        }

        private static void Log(string message, params object[] p)
        {
            try
            {
                string _message = string.Format(message, p);
                _message = string.Format("{0} {1}", DateTime.Now.ToString("MM/dd/yyyy HH:mm.fff"), _message);
                if (logWriter != null)
                {
                    lock (logWriter)
                    {
                        logWriter.WriteLine(_message);
                    } 
                }
                Console.WriteLine(_message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private static void Exit(int exitCode)
        {
            Log("Banjo stopped. Exit code {0}", exitCode);
            if (logWriter != null)
                logWriter.Close();
            Environment.Exit(exitCode);
        }

        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);
        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        private delegate bool EventHandler(CtrlType sig);

        private static bool ConsoleCtrlHandler(CtrlType sig)
        {
            switch (sig)
            {
                case CtrlType.CTRL_SHUTDOWN_EVENT:
                case CtrlType.CTRL_CLOSE_EVENT:
                    if (serverSocket != null)
                        serverSocket.Close();
                    if (process != null)
                        process.Kill();
                    while (!process.HasExited) { }
                    return true;
                default:
                    return true;
            }
        }
    }
}
