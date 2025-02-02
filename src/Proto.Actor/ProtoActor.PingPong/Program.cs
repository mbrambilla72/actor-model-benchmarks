﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ActorModelBenchmarks.Utils;
using ActorModelBenchmarks.Utils.Settings;
using Proto;
using Proto.Mailbox;

namespace ActorModelBenchmarks.ProtoActor.PingPong
{
    public static class Messages
    {
        public class Msg
        {
            public PID Sender { get; set; }

            public override string ToString()
            {
                return "msg";
            }
        }

        public class Run
        {
            public PID Sender { get; set; }

            public override string ToString()
            {
                return "run";
            }
        }

        public class Started
        {
            public PID Sender { get; set; }

            public override string ToString()
            {
                return "started";
            }
        }
    }

    internal class Program
    {
        [Flags]
        public enum PrintStats
        {
            No = 0,
            LineStart = 1,
            Stats = 2,
            StartTimeOnly = 32768
        }

        private static void Main(string[] args)
        {
            var benchmarkSettings = Configuration.GetConfiguration<PingPongSettings>("PingPongSettings");

            Start(benchmarkSettings);

            Console.Read();
        }

        private static async void Start(PingPongSettings pingPongSettings)
        {
            const int repeatFactor = 500;
            const long repeat = 30000L * repeatFactor;

            var processorCount = Environment.ProcessorCount;
            if (processorCount == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Failed to read processor count..");
                return;
            }

            ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);

            Console.WriteLine("Worker threads:          {0}", workerThreads);
            Console.WriteLine("Completion Port Threads: {0}", completionPortThreads);
            Console.WriteLine("OSVersion:               {0}", Environment.OSVersion);
            Console.WriteLine("ProcessorCount:          {0}", processorCount);
            Console.WriteLine("Actor Count:             {0}", processorCount * 2);
            Console.WriteLine("Messages sent/received:  {0}  ({0:0e0})", GetTotalMessagesReceived(repeat));
            Console.WriteLine();

            Console.Write("Actor    first start time: ");
            await Benchmark(1, 1, 1, PrintStats.StartTimeOnly, -1, -1, -1);
            Console.WriteLine(" ms");

            Console.WriteLine();
            Console.Write("Throughput, Msgs/sec, Start [ms], Total [ms]");
            Console.WriteLine();

            int timesToRun = pingPongSettings.TimesToRun;
            int[] throughputs = pingPongSettings.Throughputs;

            for (var i = 0; i < timesToRun; i++)
            {
                var redCountActorBase = 0;
                var bestThroughputActorBase = 0L;

                foreach (var throughput in throughputs)
                {
                    var result1 = await Benchmark(throughput, processorCount, repeat, PrintStats.LineStart | PrintStats.Stats, bestThroughputActorBase, redCountActorBase, i);
                    bestThroughputActorBase = result1.BestThroughput;
                    redCountActorBase = result1.RedCount;

                    Console.WriteLine();
                }

                Console.WriteLine("--------------------------");
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Done..");
        }

        private static async Task<BenchmarkResult> Benchmark(int throughput, int numberOfClients, long numberOfRepeats, PrintStats printStats, long bestThroughput, int redCount, long repeat)
        {
            var totalMessagesReceived = GetTotalMessagesReceived(numberOfRepeats);
            //times 2 since the client and the destination both send messages
            var repeatsPerClient = numberOfRepeats / numberOfClients;
            var totalWatch = Stopwatch.StartNew();


            var countdown = new CountdownEvent(numberOfClients * 2);
            var waitForStartsActorProps = Props.FromProducer(() => new WaitForStarts(countdown));
            var sys = new ActorSystem();
            var waitForStartsActor = sys.Root.SpawnNamed(waitForStartsActorProps, $"wait-for-starts-{throughput}-{repeat}");

            var clients = new List<PID>();
            var destinations = new List<PID>();
            var tasks = new List<Task>();
            var started = new Messages.Started {Sender = waitForStartsActor};

            var d = new ThreadPoolDispatcher {Throughput = throughput};

            for (var i = 0; i < numberOfClients; i++)
            {
                var destinationProps = Props.FromProducer(() => new Destination()).WithDispatcher(d);
                var destination = sys.Root.SpawnNamed(destinationProps, $"destination-{i}-{throughput}-{repeat}");

                destinations.Add(destination);

                var ts = new TaskCompletionSource<bool>();
                tasks.Add(ts.Task);

                var clientProps = Props.FromProducer(() => new ClientActor(destination, repeatsPerClient, ts)).WithDispatcher(d);
                var client = sys.Root.SpawnNamed(clientProps, $"client-{i}-{throughput}-{repeat}");

                clients.Add(client);

                sys.Root.Send(client, started);
                sys.Root.Send(destination,started);
            }

            if (!countdown.Wait(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("The system did not start in 10 seconds. Aborting.");
                return new BenchmarkResult {BestThroughput = bestThroughput, RedCount = redCount};
            }

            var setupTime = totalWatch.Elapsed;
            var sw = Stopwatch.StartNew();

            clients.ForEach(c =>
            {
                var run = new Messages.Run {Sender = c};
                sys.Root.Send(c, run);
            });

            await Task.WhenAll(tasks.ToArray());
            sw.Stop();

            totalWatch.Stop();

            var elapsedMilliseconds = sw.ElapsedMilliseconds;
            var throughputResult = elapsedMilliseconds == 0 ? -1 : totalMessagesReceived / elapsedMilliseconds * 1000;
            var foregroundColor = Console.ForegroundColor;

            if (throughputResult >= bestThroughput)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                bestThroughput = throughputResult;
                redCount = 0;
            }
            else
            {
                redCount++;
                Console.ForegroundColor = ConsoleColor.Red;
            }

            if (printStats.HasFlag(PrintStats.StartTimeOnly))
            {
                Console.Write("{0,5}", setupTime.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
            }
            else
            {
                if (printStats.HasFlag(PrintStats.LineStart))
                {
                    Console.Write("{0,10}, ", throughput);
                }

                if (printStats.HasFlag(PrintStats.Stats))
                {
                    Console.Write("{0,8}, {1,10}, {2,10}", throughputResult, setupTime.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture), totalWatch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
                }
            }

            Console.ForegroundColor = foregroundColor;

            return new BenchmarkResult {BestThroughput = bestThroughput, RedCount = redCount};
        }

        private static long GetTotalMessagesReceived(long numberOfRepeats)
        {
            return numberOfRepeats * 2;
        }

        public class Destination : IActor
        {
            public Task ReceiveAsync(IContext context)
            {
                var message = context.Message;

                switch (message)
                {
                    case Messages.Msg msg:
                        context.Send(msg.Sender,message);
                        return Task.CompletedTask;
                    case Messages.Started started:
                        context.Send(started.Sender,message);
                        return Task.CompletedTask;
                }

                return Task.CompletedTask;
            }
        }

        public class WaitForStarts : IActor
        {
            private readonly CountdownEvent _countdown;

            public WaitForStarts(CountdownEvent countdown)
            {
                _countdown = countdown;
            }

            public Task ReceiveAsync(IContext context)
            {
                if (context.Message is Messages.Started)
                    _countdown.Signal();

                return Task.CompletedTask;
            }
        }

        public class BenchmarkResult
        {
            public long BestThroughput { get; set; }

            public int RedCount { get; set; }
        }
    }
}