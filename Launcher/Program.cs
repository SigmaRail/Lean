/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Threading;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Python;
using QuantConnect.Util;

namespace QuantConnect.Lean.Launcher
{
    public class Program
    {
        private const string _collapseMessage = "Unhandled exception breaking past controls and causing collapse of algorithm node. This is likely a memory leak of an external dependency or the underlying OS terminating the LEAN engine.";
        private static LeanEngineSystemHandlers leanEngineSystemHandlers;
        private static LeanEngineAlgorithmHandlers leanEngineAlgorithmHandlers;
        private static AlgorithmNodePacket job;
        private static AlgorithmManager algorithmManager;

        static Program()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
            {
                if (e.LoadedAssembly.FullName.ToLowerInvariant().Contains("python"))
                {
                    Log.Trace($"Python for .NET Assembly: {e.LoadedAssembly.GetName()}");
                }
            };
        }

        static void Main(string[] args)
        {
            if (OS.IsWindows)
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }

            // expect first argument to be config file name
            if (args.Length > 0)
            {
                Config.MergeCommandLineArgumentsWithConfiguration(LeanArgumentParser.ParseArguments(args));
            }

            //Name thread for the profiler:
            Thread.CurrentThread.Name = "Algorithm Analysis Thread";

            Initializer.Start();
            leanEngineSystemHandlers = Initializer.GetSystemHandlers();

            //-> Pull job from QuantConnect job queue, or, pull local build:
            job = leanEngineSystemHandlers.JobQueue.NextJob(out var assemblyPath);

            if (job == null)
            {
                const string jobNullMessage = "Engine.Main(): Sorry we could not process this algorithm request.";
                Log.Error(jobNullMessage);
                Exit(1);
            }

            ApplyJobParametersToConfig(job);
            var compositeProviders = Config.Get("composite-data-providers");
            if (!string.IsNullOrWhiteSpace(compositeProviders))
            {
                Config.Set("data-provider", "QuantConnect.Lean.Engine.DataFeeds.CompositeDataProvider");
                // Config.Set("backtesting.data-provider", "QuantConnect.Lean.Engine.DataFeeds.CompositeDataProvider"); // Causes NullReferenceException with dotted key
            }
            leanEngineAlgorithmHandlers = Initializer.GetAlgorithmHandlers();

            // Activate our PythonVirtualEnvironment
            PythonInitializer.ActivatePythonVirtualEnvironment(job.PythonVirtualEnvironment);

            // if the job version doesn't match this instance version then we can't process it
            // we also don't want to reprocess redelivered jobs
            if (job.Redelivered)
            {
                Log.Error("Engine.Run(): Job Version: " + job.Version + "  Deployed Version: " + Globals.Version + " Redelivered: " + job.Redelivered);
                //Tiny chance there was an uncontrolled collapse of a server, resulting in an old user task circulating.
                //In this event kill the old algorithm and leave a message so the user can later review.
                leanEngineSystemHandlers.Api.SetAlgorithmStatus(job.AlgorithmId, AlgorithmStatus.RuntimeError, _collapseMessage);
                leanEngineSystemHandlers.Notify.SetAuthentication(job);
                leanEngineSystemHandlers.Notify.Send(new RuntimeErrorPacket(job.UserId, job.AlgorithmId, _collapseMessage));
                leanEngineSystemHandlers.JobQueue.AcknowledgeJob(job);
                Exit(1);
            }

            try
            {
                // Set our exit handler for the algorithm
                Console.CancelKeyPress += new ConsoleCancelEventHandler(ExitKeyPress);

                // Create the algorithm manager and start our engine
                algorithmManager = new AlgorithmManager(Globals.LiveMode, job);

                leanEngineSystemHandlers.LeanManager.Initialize(leanEngineSystemHandlers, leanEngineAlgorithmHandlers, job, algorithmManager);

                OS.Initialize();

                var engine = new Engine.Engine(leanEngineSystemHandlers, leanEngineAlgorithmHandlers, Globals.LiveMode);
                engine.Run(job, algorithmManager, assemblyPath, WorkerThread.Instance);
            }
            finally
            {
                var algorithmStatus = algorithmManager?.State ?? AlgorithmStatus.DeployError;

                Exit(algorithmStatus != AlgorithmStatus.Completed ? 1 : 0);
            }
        }

        public static void ExitKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            // Allow our process to resume after this event
            args.Cancel = true;

            // Stop the algorithm
            algorithmManager.SetStatus(AlgorithmStatus.Stopped);
            Log.Trace("Program.ExitKeyPress(): Lean instance has been cancelled, shutting down safely now");
        }

        public static void Exit(int exitCode)
        {
            // The job can be null if the algorithm file was not found
            if (job != null)
            {
                //Delete the message from the job queue:
                leanEngineSystemHandlers.JobQueue.AcknowledgeJob(job);
                Log.Trace("Engine.Main(): Packet removed from queue: " + job.AlgorithmId);
            }

            // clean up resources
            leanEngineSystemHandlers.DisposeSafely();
            leanEngineAlgorithmHandlers.DisposeSafely();
            Log.LogHandler.DisposeSafely();
            OS.Dispose();

            PythonInitializer.Shutdown();

            Log.Trace("Program.Main(): Exiting Lean...");
            Environment.Exit(exitCode);
        }

        private static void ApplyJobParametersToConfig(AlgorithmNodePacket algorithmJob)
        {
            if (algorithmJob.DeploymentTarget != DeploymentTarget.LocalPlatform)
            {
                SetConfigFromJobParameters(algorithmJob, "clickhouse-host", "clickhouse-host", "vera-host");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-http-port", "clickhouse-http-port", "vera-port");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-database", "clickhouse-database", "vera-database");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-user", "clickhouse-user", "vera-user");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-password", "clickhouse-password", "vera-password");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-timeout-seconds", "clickhouse-timeout-seconds");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-materialize", "clickhouse-materialize");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-trace", "clickhouse-trace");
                SetConfigFromJobParameters(algorithmJob, "clickhouse-history-pad-days", "clickhouse-history-pad-days");
            }

            SetConfigFromJobParameters(algorithmJob, "clickhouse-backtest-start", "clickhouse-backtest-start", "start-date");
            SetConfigFromJobParameters(algorithmJob, "clickhouse-backtest-end", "clickhouse-backtest-end", "end-date");
        }

        private static void SetConfigFromJobParameters(AlgorithmNodePacket algorithmJob, string configKey, params string[] parameterKeys)
        {
            foreach (var parameterKey in parameterKeys)
            {
                if (!algorithmJob.Parameters.TryGetValue(parameterKey, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                Config.Set(configKey, value);
                return;
            }
        }
    }
}
